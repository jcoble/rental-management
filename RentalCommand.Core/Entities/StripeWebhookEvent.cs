namespace RentalCommand.Core.Entities;

/// <summary>
/// Idempotency ledger for Stripe webhook events. Each received event is recorded here; a
/// duplicate delivery is detected by <see cref="EventId"/> and safely skipped.
/// </summary>
public class StripeWebhookEvent
{
    public long Id { get; set; }
    public string EventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
