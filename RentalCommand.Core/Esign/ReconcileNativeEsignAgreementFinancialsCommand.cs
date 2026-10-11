using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Esign;

public sealed record ReconcileNativeEsignAgreementFinancialsCommand(
    int SignatureRequestId,
    Guid PublicId) : IAtomicCommandData;

public sealed record ReconcileNativeEsignAgreementFinancialsResult(
    Guid PublicId,
    int SignatureRequestId,
    int LeaseAgreementId,
    int DepositChargeCount);

public sealed record ReconcileNativeEsignAgreementFinancialsBatchCommand(
    Guid RunToken,
    int BatchSize) : IAtomicCommandData;

public sealed record ReconcileNativeEsignAgreementFinancialsBatchResult(
    int DepositChargeCount);
