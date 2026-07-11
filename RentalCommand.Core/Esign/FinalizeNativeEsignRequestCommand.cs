using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Esign;

/// <summary>Attaches one rendered executed PDF and finalizes its request and lease atomically.</summary>
public sealed record FinalizeNativeEsignRequestCommand(
    int SignatureRequestId,
    string PublicId,
    Guid ClaimToken,
    string StorageKey,
    string FileName,
    long FileSize,
    string ContentSha256,
    DateTime FinalizeAttemptedAtUtc,
    DateTime CompletedAtUtc) : IAtomicCommandData;

/// <summary>
/// Raised inside the atomic transaction when an executed-document worker no longer owns the
/// request lease. The transaction (including its pending command receipt) must roll back so the
/// current owner can finalize with the same stable idempotency identity.
/// </summary>
public sealed class NativeEsignExecutionClaimLostException : InvalidOperationException
{
    public NativeEsignExecutionClaimLostException(int signatureRequestId)
        : base($"Native e-sign execution claim for request {signatureRequestId} is no longer owned.")
    {
    }
}

public sealed record FinalizeNativeEsignRequestResult(
    string PublicId,
    int SignatureRequestId,
    int LeaseId,
    int StoredFileId) : IAtomicResultData;
