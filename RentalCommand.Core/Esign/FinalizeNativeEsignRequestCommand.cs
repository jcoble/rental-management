using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Esign;

public sealed record FinalizeNativeEsignRequestCommand(
    Guid PendingUploadId,
    string RequestFingerprint,
    int SignatureRequestId,
    Guid PublicId,
    Guid ClaimToken,
    string StorageKey,
    string FileName,
    long FileSize,
    string ContentSha256) : IAtomicCommandData;

public sealed class NativeEsignExecutionClaimLostException : InvalidOperationException
{
    public NativeEsignExecutionClaimLostException(int signatureRequestId)
        : base($"Native e-sign execution claim for request {signatureRequestId} is no longer owned.") { }
}

public sealed record FinalizeNativeEsignRequestResult(
    Guid PublicId,
    int SignatureRequestId,
    int? LeaseAgreementId,
    int? LeaseAddendumId,
    int ExecutedArtifactId) : IAtomicResultData;
