namespace RentalCommand.Engine.Services;

/// <summary>
/// Generates scheduled rent <see cref="Core.Entities.Payment"/> rows for all active leases
/// within the configured lead window, idempotently (one row per lease per billing period).
/// </summary>
public interface IRentChargeService
{
    /// <summary>
    /// Scan active leases and create a <c>Scheduled</c> rent payment for the current billing
    /// period for any lease that does not already have one. Returns the number of new payments
    /// created this run.
    /// </summary>
    Task<int> GenerateAsync(CancellationToken ct = default);
}
