using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

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
public sealed class OutboxDispatchWorker : EngineWorkerBase
{
    /// <summary>Messages with this many failed attempts are no longer retried.</summary>
    public const int MaxRetryCount = 5;

    /// <summary>Maximum number of messages drained per poll cycle.</summary>
    private const int BatchSize = 50;

    protected override string WorkerName => "OutboxDispatchWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(10);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public OutboxDispatchWorker(IServiceProvider serviceProvider, ILogger<OutboxDispatchWorker> logger)
        : base(serviceProvider, logger)
    {
    }

    /// <summary>
    /// Exponential backoff: 30s * 2^retryCount, capped at 1 hour.
    /// RetryCount here is the count BEFORE this attempt (i.e. the number of prior failures),
    /// so the first retry (RetryCount=0 after first fail becomes RetryCount=1) backs off 30s,
    /// second 60s, third 120s, fourth 240s, fifth 480s (capped to 3600s).
    /// </summary>
    private static TimeSpan Backoff(int retryCount) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, retryCount)));

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var db = scopedProvider.GetRequiredService<RentalCommandDbContext>();
        var channel = scopedProvider.GetRequiredService<INotificationChannel>();
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

            try
            {
                await DispatchAsync(channel, message, cancellationToken);
                message.SentAt = DateTime.UtcNow;
                message.Error = null;
                dispatched++;

                // Persist per-message so a crash only risks this one send.
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown / cycle timeout — bail out; nothing to persist for this message yet.
                break;
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
                await channel.SendSmsAsync(to, body, ct);
                break;
            }
            case "email":
            {
                var to = GetString(root, "to") ?? GetString(root, "toEmail")
                    ?? throw new InvalidOperationException("Email outbox message is missing a 'to' address.");
                var subject = GetString(root, "subject") ?? string.Empty;
                var body = GetString(root, "body") ?? GetString(root, "message") ?? string.Empty;
                await channel.SendEmailAsync(to, subject, body, ct);
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
}
