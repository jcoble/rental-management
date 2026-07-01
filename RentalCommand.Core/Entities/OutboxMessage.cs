namespace RentalCommand.Core.Entities;

/// <summary>
/// Reliable outbound SMS/email send with retry (DB-outbox pattern; dispatched by the Engine).
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public int? PortfolioId { get; set; }
    public string MessageType { get; set; } = string.Empty;

    /// <summary>JSON payload for the message.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Optional idempotency key for producers that must not enqueue the same logical message twice
    /// (e.g. the daily briefing: <c>daily-briefing:{portfolioId}:{dateKey}</c>). Indexed so the
    /// "already queued?" check is a single indexed lookup instead of scanning + JSON-parsing payloads.
    /// Null for messages that do not need dedup.
    /// </summary>
    public string? DedupKey { get; set; }

    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public string? Error { get; set; }

    public Portfolio? Portfolio { get; set; }
}
