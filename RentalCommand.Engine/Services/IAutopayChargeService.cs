namespace RentalCommand.Engine.Services;

/// <summary>
/// Charges open, due canonical rent-ledger entries for active tenant-account autopay enrollments.
/// Provider attempts are durable before remote work; success posts one canonical receipt.
/// </summary>
public interface IAutopayChargeService
{
    /// <summary>
    /// Scan open due rent charges whose tenant account has an active autopay enrollment and submit
    /// each off-session. Returns the number of provider calls accepted this run. No-op (returns 0)
    /// when Stripe is not configured.
    /// </summary>
    Task<int> ChargeDueAsync(CancellationToken ct = default);
}
