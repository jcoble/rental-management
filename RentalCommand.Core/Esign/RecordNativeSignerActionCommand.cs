using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Esign;

public enum NativeSignerActionOutcome
{
    Applied,
    NotFound,
    Expired,
}

/// <summary>Captures one signer's legally significant signature and consent.</summary>
public sealed record RecordNativeSignatureCommand(
    string TokenHash,
    SignatureSignatureType SignatureType,
    string? TypedName,
    Guid? DrawnSignaturePendingUploadId,
    string? DrawnSignatureRequestFingerprint,
    string? DrawnSignatureStorageKey,
    long? DrawnSignatureFileSize,
    string? IpAddress,
    string? UserAgent,
    DateTime OccurredAtUtc) : IAtomicCommandData;

/// <summary>Captures one signer's refusal and closes the request and lease workflow.</summary>
public sealed record RecordNativeDeclineCommand(
    string TokenHash,
    string? Reason,
    string? IpAddress,
    string? UserAgent,
    DateTime OccurredAtUtc) : IAtomicCommandData;

/// <summary>Receipt-safe result shared by sign and decline commands.</summary>
public sealed record NativeSignerActionResult(
    NativeSignerActionOutcome Outcome,
    string? Error,
    int SignatureRequestId,
    Guid? PublicId,
    int? LeaseAgreementId,
    int? LeaseAddendumId,
    SignatureSignerStatus SignerStatus,
    SignatureRequestStatus RequestStatus,
    bool ExecutionRequired);
