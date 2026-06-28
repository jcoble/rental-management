namespace RentalCommand.Engine.Services;

/// <summary>
/// Raised when a notification was intentionally not handed to an external provider because delivery
/// is disabled or unconfigured. The outbox treats this as terminal, not retryable.
/// </summary>
public sealed class NotificationDeliverySuppressedException : Exception
{
    public NotificationDeliverySuppressedException(string message)
        : base(message)
    {
    }
}
