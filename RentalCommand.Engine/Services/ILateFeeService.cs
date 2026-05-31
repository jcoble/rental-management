namespace RentalCommand.Engine.Services;

/// <summary>
/// Assesses late fees on overdue rent payments, idempotently (one late fee per lease per
/// billing period). Controlled by <see cref="Core.Configuration.NotificationsConfig.EnableLateFees"/>;
/// default is OFF (opt-in). Per-state dollar/percent caps are applied from
/// <see cref="Core.Configuration.NotificationsConfig.StateLateFeeCaps"/>.
/// </summary>
public interface ILateFeeService
{
    /// <summary>
    /// Scan overdue rent payments past the grace window, create a <c>LateFee</c> payment for any
    /// lease that does not already have one for the billing period, and flip the rent payment to
    /// <c>Late</c>. Returns the number of late-fee payments created this run.
    /// </summary>
    Task<int> AssessAsync(CancellationToken ct = default);
}
