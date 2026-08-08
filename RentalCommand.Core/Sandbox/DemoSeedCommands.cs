using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Sandbox;

public sealed record SeedDemoPortfolioCommand(
    int PortfolioId,
    bool RequirePendingSandboxOnboarding,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string OperationKey) : IAtomicCommandData;

public sealed record SeedDemoPortfolioResult(
    int PortfolioId,
    bool AlreadyPresent,
    int OwnerEntityCount,
    int VendorCount,
    int PropertyCount,
    int UnitCount,
    int TenantCount,
    int ActiveLeaseManagementCount,
    int EndedLeaseManagementCount,
    int TenantLedgerEntryCount,
    int SecurityDepositAccountCount,
    int ExpenseCount,
    int WorkOrderCount,
    int AppointmentCount,
    int InspectionCount,
    int? LegalAgreementId);

public sealed record FinalizeDemoLegalDocumentCommand(
    int PortfolioId,
    int ActorUserId,
    int AgreementId,
    Guid IssuedPendingUploadId,
    string IssuedStoragePath,
    string IssuedRequestFingerprint,
    string IssuedFileName,
    long IssuedLength,
    string IssuedHash,
    string IssuanceFingerprint,
    Guid ExecutedPendingUploadId,
    string ExecutedStoragePath,
    string ExecutedRequestFingerprint,
    string ExecutedFileName,
    long ExecutedLength,
    string ExecutedHash,
    DateTime IssuedAtUtc,
    DateTime ExecutedAtUtc) : IAtomicCommandData;

public sealed record FinalizeDemoLegalDocumentResult(
    int PortfolioId,
    int AgreementId,
    int IssuedArtifactId,
    int ExecutedArtifactId,
    bool AlreadyFinalized,
    bool Skipped);
