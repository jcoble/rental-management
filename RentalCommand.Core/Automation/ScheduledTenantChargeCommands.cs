using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Automation;

/// <summary>
/// Retry-safe scheduled rent sweep. The run token identifies the physical sweep while
/// deterministic ledger business keys make separate/concurrent sweeps converge on one posting.
/// </summary>
public sealed record ApplyScheduledRentChargeBatchCommand(
    Guid RunToken,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    int BatchSize) : IAtomicCommandData;

public sealed record ApplyScheduledRentChargeBatchResult(
    int RentChargeCount);

public sealed record ApplyScheduledLateFeeChargeBatchCommand(
    Guid RunToken,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    int BatchSize,
    string StateLateFeeCapsJson) : IAtomicCommandData;

public sealed record ApplyScheduledLateFeeChargeBatchResult(
    int LateFeeChargeCount);
