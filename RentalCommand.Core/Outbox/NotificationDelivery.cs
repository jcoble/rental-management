namespace RentalCommand.Core.Outbox;

/// <summary>Stable identity supplied to an external delivery attempt.</summary>
public sealed record NotificationDeliveryContext(
    long OutboxMessageId,
    string IdempotencyKey,
    int AttemptCount);

/// <summary>Provider acceptance evidence persisted onto the outbox row.</summary>
public sealed record NotificationDeliveryReceipt(
    string Provider,
    string? ProviderMessageId);

/// <summary>Acceptance evidence returned by a configured SMS provider.</summary>
public sealed record SmsProviderReceipt(string? ProviderMessageId);

/// <summary>
/// The requested transport has no usable provider configuration. The outbox treats
/// this as an operator-actionable terminal block instead of a successful no-op.
/// </summary>
public sealed class NotificationDeliverySuppressedException : Exception
{
    public NotificationDeliverySuppressedException(string message)
        : base(message)
    {
    }
}
