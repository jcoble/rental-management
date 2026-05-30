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
/// the row's <see cref="OutboxMessage.SentAt"/> is set; on failure the row's
/// <see cref="OutboxMessage.RetryCount"/> is incremented and, once the retry budget is
/// exhausted, <see cref="OutboxMessage.FailedAt"/> + <see cref="OutboxMessage.Error"/> are set.
///
/// Phase 0 ships a stub <see cref="INotificationChannel"/>; concrete SMS/email providers
/// land in Phase 4.
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

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var db = scopedProvider.GetRequiredService<RentalCommandDbContext>();
        var channel = scopedProvider.GetRequiredService<INotificationChannel>();
        var logger = scopedProvider.GetRequiredService<ILogger<OutboxDispatchWorker>>();

        // Oldest-first so messages are delivered in roughly the order they were enqueued.
        var pending = await db.OutboxMessages
            .Where(m => m.SentAt == null && m.RetryCount < MaxRetryCount)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return 0;
        }

        var dispatched = 0;

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DispatchAsync(channel, message, cancellationToken);
                message.SentAt = DateTime.UtcNow;
                message.Error = null;
                dispatched++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown / cycle timeout — persist progress made so far and bail out.
                break;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.Error = ex.Message;

                if (message.RetryCount >= MaxRetryCount)
                {
                    message.FailedAt = DateTime.UtcNow;
                    logger.LogError(
                        ex,
                        "OutboxMessage {MessageId} ({MessageType}) permanently failed after {RetryCount} attempt(s)",
                        message.Id, message.MessageType, message.RetryCount);
                }
                else
                {
                    logger.LogWarning(
                        ex,
                        "OutboxMessage {MessageId} ({MessageType}) failed; will retry (attempt {RetryCount}/{Max})",
                        message.Id, message.MessageType, message.RetryCount, MaxRetryCount);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
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
