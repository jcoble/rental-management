using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Scanning;

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
    string ClientOperationId,
    string RequestFingerprint,
    string TargetEntityType,
    bool CreateBatch,
    string? BatchName,
    DateTime UploadedAtUtc,
    IReadOnlyList<FinalizeScanUploadFile> Files) : IAtomicCommandData;

public sealed record FinalizedScanDraft(
    int DraftId,
    string Status,
    string FilePath) : IAtomicResultData;

public sealed record FinalizeScanUploadResult(
    int? BatchId,
    string? BatchName,
    string TargetEntityType,
    IReadOnlyList<FinalizedScanDraft> Drafts) : IAtomicResultData;
