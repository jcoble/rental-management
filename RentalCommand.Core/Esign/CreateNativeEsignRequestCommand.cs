using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Esign;

/// <summary>Data-only signer input for one native e-sign request.</summary>
public sealed record NativeEsignSignerCommand(
    string Name,
    string Email,
    string Token) : IAtomicCommandData;

/// <summary>
/// Persists one native e-sign envelope, its immutable source document reference, signer sessions,
/// sent event, and every signing-link delivery as one receipt-backed database command.
/// </summary>
public sealed record CreateNativeEsignRequestCommand(
    Guid PendingUploadId,
    string RequestFingerprint,
    int PortfolioId,
    int LeaseId,
    string PublicId,
    string DocumentName,
    string? Subject,
    string StorageKey,
    long FileSize,
    int? DocumentTemplateId,
    int? DocumentTemplateVersion,
    string? TemplateFieldSnapshotJson,
    string WebBaseUrl,
    DateTime CreatedAtUtc,
    DateTime LinkExpiresAtUtc,
    IReadOnlyList<NativeEsignSignerCommand> Signers) : IAtomicCommandData;

/// <summary>Receipt-safe identity of the envelope package created by the command.</summary>
public sealed record CreateNativeEsignRequestResult(
    string PublicId,
    int SignatureRequestId,
    int OriginalStoredFileId) : IAtomicResultData;
