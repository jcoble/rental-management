using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Core.Esign;

public sealed record NativeEsignSignerCommand(int AgreementSignerId) : IAtomicCommandData;
public sealed record NativeEsignAddendumSignerCommand(int AddendumSignerId) : IAtomicCommandData;

/// <summary>Freezes a draft Agreement, its PDF, and one native signing packet atomically.</summary>
public sealed record IssueLeaseAgreementCommand(
    Guid PendingUploadId,
    int ExpectedDocumentSourceVersionId,
    string IssuanceFingerprint,
    int PortfolioId,
    int LeaseManagementId,
    int LeaseAgreementId,
    int ExpectedDraftRevision,
    string DeliveryIdempotencyKey,
    string Subject,
    string StorageKey,
    string FileName,
    long FileSize,
    string ContentSha256,
    string WebBaseUrl,
    IReadOnlyList<NativeEsignSignerCommand> Signers,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision) : ILeaseAgreementDraftCommand;

public sealed record IssueLeaseAgreementResult(
    Guid PublicId,
    int LeaseManagementId,
    int LeaseAgreementId,
    int SignatureRequestId,
    int IssuedArtifactId);

/// <summary>Freezes a draft Addendum, its PDF, and one native signing packet atomically.</summary>
public sealed record IssueLeaseAddendumCommand(
    Guid PendingUploadId,
    int ExpectedDocumentSourceVersionId,
    string IssuanceFingerprint,
    int PortfolioId,
    int LeaseManagementId,
    int LeaseAddendumId,
    int ExpectedDraftRevision,
    string DeliveryIdempotencyKey,
    string Subject,
    string StorageKey,
    string FileName,
    long FileSize,
    string ContentSha256,
    string WebBaseUrl,
    IReadOnlyList<NativeEsignAddendumSignerCommand> Signers,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision) : ILeaseAddendumCommand;

public sealed record IssueLeaseAddendumResult(
    Guid PublicId,
    int LeaseManagementId,
    int LeaseAddendumId,
    int SignatureRequestId,
    int IssuedArtifactId);
