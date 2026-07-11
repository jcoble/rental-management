using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Documents;

/// <summary>
/// Canonical document targets admitted by the general attachment hub. The wire names intentionally
/// match the existing polymorphic <c>StoredFile.EntityType</c> values.
/// </summary>
public enum StoredDocumentTarget
{
    Property,
    Unit,
    Tenant,
    Lease,
    Payment,
    Expense,
    Vendor,
    WorkOrder,
    Appointment,
    Inspection,
    SecurityDeposit,
    OwnerEntity,
}

public enum StoredDocumentMutationOutcome
{
    Created,
    Deleted,
    NotFound,
}

/// <summary>
/// Persists one already-uploaded blob as a general document. Blob I/O is deliberately excluded from
/// this command. A durable pending-upload admission owns the deterministic blob key until this command
/// finalizes it or the stale-upload scavenger safely removes it.
/// </summary>
public sealed record CreateStoredDocumentCommand(
    Guid PendingUploadId,
    int PortfolioId,
    StoredDocumentTarget Target,
    int EntityId,
    int UserId,
    int? TenantId,
    bool IsStaff,
    string ClientOperationId,
    string RequestFingerprint,
    string ContentSha256,
    string FileName,
    string StoragePath,
    string ContentType,
    long SizeBytes,
    DateTime UploadedAtUtc) : IAtomicCommandData;

public sealed record CreateStoredDocumentResult(
    StoredDocumentMutationOutcome Outcome,
    int StoredFileId,
    string EntityType,
    int EntityId,
    string FileName,
    string StoragePath,
    string ContentType,
    long SizeBytes,
    DateTime UploadedAtUtc) : IAtomicResultData;

/// <summary>
/// Soft-deletes a document row and stages durable physical-blob cleanup in the same database
/// transaction. The Engine performs the storage call only after commit.
/// </summary>
public sealed record DeleteStoredDocumentCommand(
    int PortfolioId,
    int StoredFileId,
    int UserId,
    int? TenantId,
    bool IsStaff,
    string ClientOperationId,
    DateTime DeletedAtUtc) : IAtomicCommandData;

public sealed record DeleteStoredDocumentResult(
    StoredDocumentMutationOutcome Outcome,
    int StoredFileId,
    string EntityType,
    int EntityId,
    string FileName,
    string StoragePath,
    DateTime DeletedAtUtc) : IAtomicResultData;
