using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;

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

public sealed class NativeEsignLegalTransitionConflictException : InvalidOperationException
{
    public NativeEsignLegalTransitionConflictException(AtomicLegalExecutionTransitionOutcome outcome)
        : base($"Native e-sign legal transition was rejected: {outcome}.")
    {
        Outcome = outcome;
    }

    public NativeEsignLegalTransitionConflictException(
        int signatureRequestId,
        AtomicLegalExecutionTransitionOutcome outcome)
        : base($"Native e-sign legal transition for request {signatureRequestId} was rejected: {outcome}.")
    {
        Outcome = outcome;
    }

    public AtomicLegalExecutionTransitionOutcome Outcome { get; }
}

public sealed record FinalizeNativeEsignRequestResult(
    Guid PublicId,
    int SignatureRequestId,
    int? LeaseAgreementId,
    int? LeaseAddendumId,
    int ExecutedArtifactId);
