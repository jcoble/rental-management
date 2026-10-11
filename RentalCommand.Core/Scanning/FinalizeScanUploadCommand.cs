using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Scanning;

public sealed record ScanCaptureContextData(
    RentalCommand.Core.Enums.WorkspaceExperience? Experience,
    int? AccessContextId,
    long? AccessRevision,
    int? PropertyId,
    int? UnitId,
    int? LeaseManagementId,
    int? LeaseAgreementId,
    int? TenantAccountId,
    long? TenantLedgerEntryId,
    int? WorkOrderId,
    int? ApplicationId,
    int? RentalListingId,
    string? SourceLabel) : IAtomicCommandData;

public sealed record FinalizeScanUploadFile(
    Guid SourcePendingUploadId,
    string SourceStoragePath,
    string SourceFileName,
    string SourceContentType,
    long SourceSizeBytes,
    string SourceSha256,
    Guid? ThumbnailPendingUploadId,
    string? ThumbnailStoragePath,
    string? ThumbnailFileName,
    long? ThumbnailSizeBytes,
    string? ThumbnailSha256) : IAtomicCommandData;

/// <summary>
/// Finalizes blobs that were durably admitted and uploaded before entering the database transaction.
/// A batch is represented by one command so its StoredFiles, ScanDrafts, optional ScanBatch, audit,
/// pending-upload transitions, and receipt either all commit or all roll back.
/// </summary>
public sealed record FinalizeScanUploadCommand(
    int PortfolioId,
    int UploadedByUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string ClientOperationId,
    string RequestFingerprint,
    string TargetEntityType,
    bool CreateBatch,
    string? BatchName,
    [property: AtomicFingerprintIgnore] DateTime UploadedAtUtc,
    IReadOnlyList<FinalizeScanUploadFile> Files,
    ScanCaptureContextData? CaptureContext = null) : IAtomicCommandData;

public sealed record FinalizedScanDraft(
    int DraftId,
    string Status,
    string FilePath);

public sealed record FinalizeScanUploadResult(
    int? BatchId,
    string? BatchName,
    string TargetEntityType,
    IReadOnlyList<FinalizedScanDraft> Drafts);
