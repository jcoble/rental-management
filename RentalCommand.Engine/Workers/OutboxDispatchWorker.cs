using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Polls <see cref="OutboxMessage"/> rows that are unsent and still retryable, and routes
/// each one to the registered <see cref="INotificationChannel"/> (SMS or email). On success
/// the row's <see cref="OutboxMessage.SentAt"/> is set; on failure <see cref="OutboxMessage.FailedAt"/>
/// is updated, <see cref="OutboxMessage.RetryCount"/> is incremented, and exponential backoff
/// prevents the next attempt until <c>FailedAt + Backoff(RetryCount)</c> has elapsed. Once the
/// retry budget is exhausted the row stays permanently failed.
///
/// Crash-safety: progress is persisted after each individual message so a hard restart only risks
/// the single in-flight message rather than the whole batch.
/// </summary>
public class OutboxDispatchWorker : EngineWorkerBase
{
    /// <summary>Messages with this many failed attempts are no longer retried.</summary>
    public const int MaxRetryCount = 5;

    /// <summary>Maximum number of messages drained per poll cycle.</summary>
    private const int BatchSize = 50;

    /// <summary>
    /// How many times to attempt the isolated SentAt commit after a successful external send
    /// before giving up (and accepting that the message will be re-sent next cycle).
    /// </summary>
    private const int PersistRetryAttempts = 3;

    /// <summary>Short delay between SentAt-commit retry attempts.</summary>
    private static readonly TimeSpan PersistRetryDelay = TimeSpan.FromMilliseconds(200);

    protected override string WorkerName => "OutboxDispatchWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(10);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public OutboxDispatchWorker(IServiceProvider serviceProvider, ILogger<OutboxDispatchWorker> logger)
        : base(serviceProvider, logger)
    {
    }

    /// <summary>
    /// Exponential backoff: 30s * 2^retryCount, capped at 1 hour.
    /// The backoff check (see <see cref="ExecuteCycleAsync"/>) passes the message's CURRENT
    /// <see cref="OutboxMessage.RetryCount"/> — i.e. the count AFTER the failure was recorded
    /// and incremented. So after the first failure RetryCount=1 and the wait before the next
    /// attempt is Backoff(1)=60s; then RetryCount=2 → 120s, 3 → 240s, 4 → 480s, 5 → 960s
    /// (all capped to 3600s). RetryCount=5 reaches <see cref="MaxRetryCount"/> and is no longer
    /// retried, so the largest backoff actually used is Backoff(4)=480s.
    /// </summary>
    private static TimeSpan Backoff(int retryCount) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, retryCount)));

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var db = scopedProvider.GetRequiredService<RentalCommandDbContext>();
        var channel = scopedProvider.GetRequiredService<INotificationChannel>();
        var pushSender = scopedProvider.GetRequiredService<IPushSender>();
        var logger = scopedProvider.GetRequiredService<ILogger<OutboxDispatchWorker>>();

        // Load all candidates (unsent, within retry budget) — oldest-first.
        // Backoff filtering is applied in C# below to avoid a complex DB expression.
        var candidates = await db.OutboxMessages
            .Where(m => m.SentAt == null && m.RetryCount < MaxRetryCount)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var dispatched = 0;

        foreach (var message in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Backoff check: skip messages whose last-failure timestamp is too recent.
            if (message.FailedAt is not null &&
                message.FailedAt.Value + Backoff(message.RetryCount) > now)
            {
                continue;
            }

            var isPush = string.Equals(message.MessageType?.Trim(), "push", StringComparison.OrdinalIgnoreCase);

            try
            {
                // At-least-once semantics: we send the external message FIRST, then persist
                // SentAt. We must NOT mark SentAt before the send — a pre-send crash would
                // then silently drop the message. The cost of send-then-persist is a narrow
                // duplicate-send window: if the process dies (or the DB write fails) AFTER a
                // successful external send but BEFORE SentAt is committed, the message is
                // re-sent next cycle. We minimise that window by committing SentAt in an
                // isolated, retried SaveChanges immediately after the successful send.
                if (isPush)
                {
                    await DispatchPushAsync(db, pushSender, message, logger, cancellationToken);
                }
                else
                {
                    await DispatchAsync(channel, message, cancellationToken);
                }
                message.SentAt = DateTime.UtcNow;
                message.FailedAt = null;
                message.Error = null;
                dispatched++;

                // Persist SentAt per-message. Retry a transient DB hiccup a few times so a
                // momentary blip doesn't cause an avoidable duplicate send on the next cycle.
                // Use CancellationToken.None: the external send already happened, so we must
                // try hard to record it even if the cycle is being cancelled.
                if (!await PersistSentWithRetryAsync(db, logger, message))
                {
                    // The send succeeded but SentAt could not be committed after several
                    // attempts. The message will be re-sent next cycle (duplicate SMS/email).
                    // Log loudly at Error level so this rare duplicate is observable.
                    logger.LogError(
                        "OutboxMessage {MessageId} ({MessageType}) was sent successfully but SentAt " +
                        "could NOT be persisted after {Attempts} attempts — it will be RE-SENT next " +
                        "cycle (duplicate delivery). Manual reconciliation may be required.",
                        message.Id, message.MessageType, PersistRetryAttempts);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown / cycle timeout — bail out; nothing to persist for this message yet.
                break;
            }
            catch (NotificationDeliverySuppressedException ex)
            {
                message.SentAt = DateTime.UtcNow;
                message.FailedAt = null;
                message.Error = ex.Message;
                dispatched++;

                logger.LogInformation(
                    ex,
                    "OutboxMessage {MessageId} ({MessageType}) delivery suppressed without retry.",
                    message.Id, message.MessageType);

                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Set FailedAt on EVERY failure so backoff can be computed from the most recent attempt.
                message.FailedAt = DateTime.UtcNow;
                message.RetryCount++;
                message.Error = ex.Message;

                if (message.RetryCount >= MaxRetryCount)
                {
                    logger.LogError(
                        ex,
                        "OutboxMessage {MessageId} ({MessageType}) permanently failed after {RetryCount} attempt(s)",
                        message.Id, message.MessageType, message.RetryCount);
                }
                else
                {
                    logger.LogWarning(
                        ex,
                        "OutboxMessage {MessageId} ({MessageType}) failed; will retry (attempt {RetryCount}/{Max}) after {Backoff}",
                        message.Id, message.MessageType, message.RetryCount, MaxRetryCount,
                        Backoff(message.RetryCount));
                }

                // Persist the failure record per-message; use CancellationToken.None so a
                // cycle-timeout cancel doesn't discard the failure state.
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        return dispatched;
    }

    /// <summary>
    /// Commits the in-memory SentAt change for a single just-sent message, retrying a
    /// transient DB failure up to <see cref="PersistRetryAttempts"/> times with a short delay.
    /// Returns <c>true</c> if SentAt was persisted, <c>false</c> if every attempt failed (in
    /// which case the message will be re-sent next cycle — the at-least-once duplicate window).
    /// Uses <see cref="CancellationToken.None"/> because the external send has already happened
    /// and we must try hard to record it regardless of cycle cancellation.
    /// </summary>
    private static async Task<bool> PersistSentWithRetryAsync(
        RentalCommandDbContext db, ILogger<OutboxDispatchWorker> logger, OutboxMessage message)
    {
        for (var attempt = 1; attempt <= PersistRetryAttempts; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "OutboxMessage {MessageId} ({MessageType}) was sent but persisting SentAt failed " +
                    "(attempt {Attempt}/{MaxAttempts}).",
                    message.Id, message.MessageType, attempt, PersistRetryAttempts);

                if (attempt < PersistRetryAttempts)
                {
                    await Task.Delay(PersistRetryDelay, CancellationToken.None);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Fans a single <c>push</c> outbox message out to every device token registered for the
    /// message's portfolio. The payload carries <c>title</c>/<c>body</c> plus a deep-link data map
    /// (<c>actionUrl</c>, <c>type</c>, <c>relatedEntityType</c>, <c>relatedEntityId</c>) that the
    /// mobile client routes on when the user taps the notification. Tokens the provider reports as
    /// permanently invalid (app uninstalled / token rotated) are pruned. A transient provider error
    /// on any token propagates so the whole message retries (at-least-once; a duplicate push is
    /// acceptable). When no provider is configured the sender suppresses (logs) and the message is
    /// marked sent.
    /// </summary>
    private static async Task DispatchPushAsync(
        RentalCommandDbContext db,
        IPushSender pushSender,
        OutboxMessage message,
        ILogger<OutboxDispatchWorker> logger,
        CancellationToken ct)
    {
        if (message.PortfolioId is not int portfolioId || portfolioId <= 0)
        {
            // No portfolio → nothing to target; treat as a no-op (marked sent by the caller).
            return;
        }

        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(message.Payload) ? "{}" : message.Payload);
        var root = doc.RootElement;

        var title = GetString(root, "title") ?? "Rental Command";
        var body = GetString(root, "body") ?? string.Empty;

        var data = new Dictionary<string, string>();
        foreach (var key in new[] { "actionUrl", "type", "relatedEntityType", "relatedEntityId" })
        {
            var value = GetString(root, key);
            if (!string.IsNullOrWhiteSpace(value)) data[key] = value;
        }

        var targetUserIds = GetTargetUserIds(root);
        var tokenQuery = db.DeviceTokens
            .Where(d => d.PortfolioId == portfolioId);
        if (targetUserIds.Count > 0)
        {
            tokenQuery = tokenQuery.Where(d => targetUserIds.Contains(d.UserId));
        }

        var tokens = await tokenQuery
            .Select(d => d.Token)
            .ToListAsync(ct);

        if (tokens.Count == 0)
        {
            logger.LogInformation(
                "[push] OutboxMessage {MessageId} — no registered devices for portfolio {PortfolioId}{Target}.",
                message.Id, portfolioId,
                targetUserIds.Count == 0 ? string.Empty : $" target user(s) {string.Join(",", targetUserIds)}");
            return;
        }

        var invalidTokens = new List<string>();
        foreach (var token in tokens)
        {
            var result = await pushSender.SendAsync(token, title, body, data, ct);
            if (result.TokenInvalid) invalidTokens.Add(token);
        }

        if (invalidTokens.Count > 0)
        {
            await db.DeviceTokens
                .Where(d => d.PortfolioId == portfolioId && invalidTokens.Contains(d.Token))
                .ExecuteDeleteAsync(ct);
            logger.LogInformation(
                "[push] Pruned {Count} dead device token(s) for portfolio {PortfolioId}.",
                invalidTokens.Count, portfolioId);
        }
    }

    /// <summary>
    /// Routes a single outbox message to the SMS or email transport based on its
    /// <see cref="OutboxMessage.MessageType"/> ("sms" / "email"). The payload is a JSON
    /// document whose shape depends on the channel.
    /// </summary>
    private static async Task DispatchAsync(INotificationChannel channel, OutboxMessage message, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(message.Payload) ? "{}" : message.Payload);
        var root = doc.RootElement;

        var type = message.MessageType?.Trim().ToLowerInvariant();
        switch (type)
        {
            case "sms":
            {
                var to = GetString(root, "to") ?? GetString(root, "toPhoneNumber")
                    ?? throw new InvalidOperationException("SMS outbox message is missing a 'to' phone number.");
                var body = GetString(root, "message") ?? GetString(root, "body") ?? string.Empty;
                // Pass the portfolio so the dispatcher resolves THAT landlord's BYO SMS provider
                // (falling back to platform env when they haven't configured one).
                await channel.SendSmsAsync(to, body, message.PortfolioId, ct);
                break;
            }
            case "email":
            {
                var to = GetString(root, "to") ?? GetString(root, "toEmail")
                    ?? throw new InvalidOperationException("Email outbox message is missing a 'to' address.");
                var subject = GetString(root, "subject") ?? string.Empty;
                var body = GetString(root, "body") ?? GetString(root, "message") ?? string.Empty;
                // Optional pre-composed HTML alternative (e.g. auth emails with a "Here" hyperlink).
                var htmlBody = GetString(root, "htmlBody");
                await channel.SendEmailAsync(to, subject, body, htmlBody, ct);
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"Unknown outbox message type '{message.MessageType}'. Expected 'sms' or 'email'.");
        }
    }

    private static string? GetString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<int> GetTargetUserIds(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return [];

        if (root.TryGetProperty("userIds", out var userIds) &&
            userIds.ValueKind == JsonValueKind.Array)
        {
            return userIds.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _))
                .Select(e => e.GetInt32())
                .Where(id => id > 0)
                .Distinct()
                .ToList();
        }

        if (root.TryGetProperty("userId", out var userId) &&
            userId.ValueKind == JsonValueKind.Number &&
            userId.TryGetInt32(out var id) &&
            id > 0)
        {
            return [id];
        }

        return [];
    }
}
