using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data.Outbox;
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
        var logger = scopedProvider.GetRequiredService<ILogger<OutboxDispatchWorker>>();
        var now = DateTime.UtcNow;
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        var claims = await store.ClaimAsync(owner, now, ClaimLease, BatchSize, cancellationToken);
        var accepted = 0;

        foreach (var claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var provider = await DispatchAsync(channel, pushSender, claim, cancellationToken);
                var changed = await store.MarkAcceptedAsync(
                    claim.Id,
                    claim.ClaimToken,
                    DateTime.UtcNow,
                    provider,
                    providerMessageId: null,
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
                        claim.Id, provider, claim.ClaimToken);
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

                var next = DateTime.UtcNow.Add(Backoff(claim.AttemptCount));
                var changed = await store.MarkRetryableAsync(
                    claim.Id, claim.ClaimToken, next, ex.Message, CancellationToken.None);
                if (changed == 1)
                {
                    logger.LogWarning(
                        ex,
                        "Outbox delivery {MessageId} failed on attempt {Attempt}; retry at {NextAttemptAtUtc}.",
                        claim.Id, claim.AttemptCount, next);
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

    private static async Task<string> DispatchAsync(
        INotificationChannel channel,
        IPushSender pushSender,
        OutboxClaim claim,
        CancellationToken ct)
    {
        using var document = JsonDocument.Parse(claim.Payload);
        var root = document.RootElement;

        switch (claim.MessageType.Trim().ToLowerInvariant())
        {
            case "sms":
                await channel.SendSmsAsync(
                    Required(root, "to", "toPhoneNumber"),
                    Optional(root, "message", "body") ?? string.Empty,
                    claim.PortfolioId,
                    ct);
                return "sms";

            case "email":
                await channel.SendEmailAsync(
                    Required(root, "to", "toEmail"),
                    Optional(root, "subject") ?? string.Empty,
                    Optional(root, "body", "message") ?? string.Empty,
                    Optional(root, "htmlBody"),
                    ct);
                return "email";

            case "push":
            {
                var token = Required(root, "deviceToken");
                var data = new Dictionary<string, string>();
                foreach (var key in new[] { "actionUrl", "type", "relatedEntityType", "relatedEntityId" })
                {
                    var value = Optional(root, key);
                    if (!string.IsNullOrWhiteSpace(value)) data[key] = value;
                }

                var result = await pushSender.SendAsync(
                    token,
                    Optional(root, "title") ?? "Rental Command",
                    Optional(root, "body") ?? string.Empty,
                    data,
                    ct);
                if (result.TokenInvalid)
                    throw new OutboxPermanentDeliveryException("The destination push token is no longer valid.");
                if (!result.Sent)
                    throw new NotificationDeliverySuppressedException(
                        "Push delivery is not configured. No external provider accepted this message.");
                return "fcm";
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
            claim.Id, claim.ClaimToken, DateTime.UtcNow, failureKind, error, CancellationToken.None);
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

    private sealed class OutboxPermanentDeliveryException : Exception
    {
        public OutboxPermanentDeliveryException(string message) : base(message) { }
    }
}
