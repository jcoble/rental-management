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
    LeaseAgreement,
    LegalDocumentArtifact,
    TenantAccount,
    TenantLedgerEntry,
    Expense,
    Vendor,
    WorkOrder,
    Appointment,
    Inspection,
    SecurityDepositAccount,
    OwnerEntity,
}

public enum StoredDocumentMutationOutcome
{
    Created,
    ReusedExisting,
    Deleted,
    NotFound,
}

public sealed record StoredDocumentManagementAccess(
    Guid SessionId,
    int UserId,
    int AccessContextId,
    long AccessRevision) : IAtomicCommandData;

/// <summary>
/// Persists one already-uploaded blob as a general document. Blob I/O is deliberately excluded from
/// this command. A durable pending-upload admission owns the deterministic blob key until this command
/// finalizes it or the stale-upload scavenger safely removes it.
/// </summary>
public sealed record CreateStoredDocumentCommand(
    Guid PendingUploadId,
    int PortfolioId,
    StoredDocumentTarget Target,
    long EntityId,
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
    DateTime UploadedAtUtc,
    DateTime SecurityAtUtc,
    StoredDocumentManagementAccess? ManagementAccess = null) : IAtomicCommandData;

public sealed record CreateStoredDocumentResult(
    StoredDocumentMutationOutcome Outcome,
    int StoredFileId,
    string EntityType,
    long EntityId,
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
    DateTime DeletedAtUtc,
    DateTime SecurityAtUtc,
    StoredDocumentManagementAccess? ManagementAccess = null) : IAtomicCommandData;

public sealed record DeleteStoredDocumentResult(
    StoredDocumentMutationOutcome Outcome,
    int StoredFileId,
    string EntityType,
    long EntityId,
    string FileName,
    string StoragePath,
    DateTime DeletedAtUtc) : IAtomicResultData;
