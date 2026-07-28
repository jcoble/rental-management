using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Automation;

/// <summary>
/// One retry-safe scheduled-billing sweep. The run token identifies the physical sweep while
/// deterministic ledger business keys make separate/concurrent sweeps converge on one posting.
/// </summary>
public sealed record ApplyScheduledTenantChargeBatchCommand(
    Guid RunToken,
    DateTime BusinessNowUtc,
    int BatchSize,
    bool IncludeRentCharges,
    bool IncludeLateFeeCharges,
    string StateLateFeeCapsJson) : IAtomicCommandData;

public sealed record ApplyScheduledTenantChargeBatchResult(
    int RentChargeCount,
    int LateFeeChargeCount) : IAtomicResultData
{
    public int TotalCount => RentChargeCount + LateFeeChargeCount;
}
