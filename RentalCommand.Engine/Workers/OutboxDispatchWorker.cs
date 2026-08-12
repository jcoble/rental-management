using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Data.Outbox;
using RentalCommand.Data;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Claims ready deliveries with a database lease, calls providers with no database transaction
/// open, and conditionally finalizes with the claim token. A zero-row finalization means ownership
/// was lost and is never treated as success.
/// </summary>
public class OutboxDispatchWorker : EngineWorkerBase
{
    public const int MaxAttemptCount = 5;
    private const int BatchSize = 50;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    protected override string WorkerName => nameof(OutboxDispatchWorker);
    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(10);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public OutboxDispatchWorker(IServiceProvider serviceProvider, ILogger<OutboxDispatchWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken)
    {
        var store = scopedProvider.GetRequiredService<IOutboxClaimStore>();
        var channel = scopedProvider.GetRequiredService<INotificationChannel>();
        var pushSender = scopedProvider.GetRequiredService<IPushSender>();
        var fileStorage = scopedProvider.GetRequiredService<IFileStorage>();
        var dataUpdate = scopedProvider.GetRequiredService<IDataUpdateService>();
        var db = scopedProvider.GetRequiredService<RentalCommandDbContext>();
        var logger = scopedProvider.GetRequiredService<ILogger<OutboxDispatchWorker>>();
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        var claims = await store.ClaimAsync(owner, ClaimLease, BatchSize, cancellationToken);
        var accepted = 0;

        foreach (var claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var receipt = await DispatchAsync(
                    channel, pushSender, fileStorage, dataUpdate, db, claim, cancellationToken);
                var changed = await store.MarkAcceptedAsync(
                    claim.Id,
                    claim.ClaimToken,
                    receipt.Provider,
                    receipt.ProviderMessageId,
                    CancellationToken.None);
                if (changed == 1)
                {
                    accepted++;
                }
                else
                {
                    logger.LogError(
                        "Outbox delivery {MessageId} was accepted by {Provider}, but claim {ClaimToken} " +
                        "was no longer current. Provider reconciliation is required.",
                        claim.Id, receipt.Provider, claim.ClaimToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (NotificationDeliverySuppressedException ex)
            {
                await FinalizeTerminalAsync(
                    store, claim, OutboxFailureKind.ConfigurationBlocked, ex.Message, logger);
            }
            catch (OutboxPermanentDeliveryException ex)
            {
                await FinalizeTerminalAsync(store, claim, OutboxFailureKind.Permanent, ex.Message, logger);
            }
            catch (Exception ex)
            {
                if (claim.AttemptCount >= MaxAttemptCount)
                {
                    await FinalizeTerminalAsync(
                        store,
                        claim,
                        OutboxFailureKind.Permanent,
                        $"Retry budget exhausted: {ex.Message}",
                        logger);
                    continue;
                }

                var retryDelay = Backoff(claim.AttemptCount);
                var changed = await store.MarkRetryableAsync(
                    claim.Id, claim.ClaimToken, retryDelay, ex.Message, CancellationToken.None);
                if (changed == 1)
                {
                    logger.LogWarning(
                        ex,
                        "Outbox delivery {MessageId} failed on attempt {Attempt}; retry after {RetryDelay}.",
                        claim.Id, claim.AttemptCount, retryDelay);
                }
                else
                {
                    logger.LogWarning(
                        ex,
                        "Outbox delivery {MessageId} failed after its claim was lost; stale failure was ignored.",
                        claim.Id);
                }
            }
        }

        return accepted;
    }

    private static TimeSpan Backoff(int attemptCount) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, attemptCount - 1))));

    private static async Task<NotificationDeliveryReceipt> DispatchAsync(
        INotificationChannel channel,
        IPushSender pushSender,
        IFileStorage fileStorage,
        IDataUpdateService dataUpdate,
        RentalCommandDbContext db,
        OutboxClaim claim,
        CancellationToken ct)
    {
        using var document = JsonDocument.Parse(claim.Payload);
        var root = document.RootElement;
        var delivery = new NotificationDeliveryContext(
            claim.Id,
            claim.IdempotencyKey,
            claim.AttemptCount);

        switch (claim.MessageType.Trim().ToLowerInvariant())
        {
            case "sms":
                return await channel.SendSmsAsync(
                    Required(root, "to", "toPhoneNumber"),
                    Optional(root, "message", "body") ?? string.Empty,
                    delivery,
                    claim.PortfolioId,
                    ct);

            case "email":
                return await channel.SendEmailAsync(
                    Required(root, "to", "toEmail"),
                    Optional(root, "subject") ?? string.Empty,
                    Optional(root, "body", "message") ?? string.Empty,
                    delivery,
                    Optional(root, "htmlBody"),
                    ct);

            case "push":
            {
                var token = Required(root, "deviceToken");
                var data = new Dictionary<string, string>();
                if (root.TryGetProperty("navigationIntent", out var navigationIntent)
                    && navigationIntent.ValueKind == JsonValueKind.Object)
                {
                    data["navigationIntent"] = navigationIntent.GetRawText();
                }

                var result = await pushSender.SendAsync(
                    token,
                    Optional(root, "title") ?? "Rental Command",
                    Optional(root, "body") ?? string.Empty,
                    data,
                    delivery,
                    ct);
                if (result.TokenInvalid)
                    throw new OutboxPermanentDeliveryException("The destination push token is no longer valid.");
                if (!result.Sent)
                    throw new NotificationDeliverySuppressedException(
                        "Push delivery is not configured. No external provider accepted this message.");
                return new NotificationDeliveryReceipt("fcm", result.ProviderMessageId);
            }

            case "blob-delete":
            {
                var storagePath = Required(root, "storagePath");
                var storedFileId = OptionalInt(root, "storedFileId");
                var hasLiveReference = storedFileId is int id
                    ? await db.StoredFiles
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .AnyAsync(file => file.Id != id
                            && file.FilePath == storagePath
                            && file.DeletedAt == null, ct)
                    : await db.StoredFiles
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .AnyAsync(file => file.FilePath == storagePath
                            && file.DeletedAt == null, ct);
                if (hasLiveReference)
                {
                    return new NotificationDeliveryReceipt(
                        "file-storage-reference-preserved", $"outbox-{claim.Id}");
                }
                await fileStorage.DeleteAsync(storagePath, ct);
                return new NotificationDeliveryReceipt("file-storage", $"outbox-{claim.Id}");
            }

            case "data-update":
            {
                var entityType = Required(root, "entityType");
                var entityId = RequiredInt(root, "entityId");
                var data = root.TryGetProperty("data", out var value)
                    ? value.Clone()
                    : JsonSerializer.SerializeToElement(new { });
                var portfolioId = claim.PortfolioId
                    ?? throw new OutboxPermanentDeliveryException("Data-update outbox row has no portfolio.");
                if (string.Equals(Optional(root, "operation"), "delete", StringComparison.OrdinalIgnoreCase))
                {
                    await dataUpdate.BroadcastEntityDeleteAsync(portfolioId, entityType, entityId, ct);
                }
                else
                {
                    await dataUpdate.BroadcastEntityUpdateAsync(
                        portfolioId, entityType, entityId, data, ct);
                }
                return new NotificationDeliveryReceipt("postgres-notify", $"outbox-{claim.Id}");
            }

            default:
                throw new OutboxPermanentDeliveryException(
                    $"Unknown outbox message type '{claim.MessageType}'.");
        }
    }

    private static async Task FinalizeTerminalAsync(
        IOutboxClaimStore store,
        OutboxClaim claim,
        OutboxFailureKind failureKind,
        string error,
        ILogger logger)
    {
        var changed = await store.MarkDeadLetteredAsync(
            claim.Id, claim.ClaimToken, failureKind, error, CancellationToken.None);
        if (changed == 1)
        {
            logger.LogError(
                "Outbox delivery {MessageId} ended as {FailureKind}: {Error}",
                claim.Id, failureKind, error);
        }
        else
        {
            logger.LogWarning(
                "Terminal result for outbox delivery {MessageId} was ignored because its claim was stale.",
                claim.Id);
        }
    }

    private static string Required(JsonElement root, params string[] names) =>
        Optional(root, names)
        ?? throw new OutboxPermanentDeliveryException(
            $"Outbox payload is missing required field '{string.Join("' or '", names)}'.");

    private static string? Optional(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static int RequiredInt(JsonElement root, string name)
    {
        return OptionalInt(root, name)
            ?? throw new OutboxPermanentDeliveryException(
                $"Outbox payload is missing required integer field '{name}'.");
    }

    private static int? OptionalInt(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.TryGetInt32(out var result) && result > 0)
        {
            return result;
        }

        throw new OutboxPermanentDeliveryException(
            $"Outbox payload is missing required integer field '{name}'.");
    }

    private sealed class OutboxPermanentDeliveryException : Exception
    {
        public OutboxPermanentDeliveryException(string message) : base(message) { }
    }
}
