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

    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public string? Error { get; set; }

    public Portfolio? Portfolio { get; set; }
}
