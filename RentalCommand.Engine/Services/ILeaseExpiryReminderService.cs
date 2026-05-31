namespace RentalCommand.Engine.Services;

/// <summary>
/// Sends one-time lease-expiry reminder messages to the property owner (or their linked user
/// account) for every active lease whose end date falls within the configured look-ahead window.
/// Idempotency is tracked via <c>Lease.ExpiryReminderSentAt</c>.
/// </summary>
public interface ILeaseExpiryReminderService
{
    /// <summary>
    /// Scan active leases that expire within the configured window and have not yet been reminded,
    /// then publish an owner-facing email (or SMS fallback). Returns the number of reminders sent.
    /// </summary>
    Task<int> RemindAsync(CancellationToken ct = default);
}
