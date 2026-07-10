using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Esign;

/// <summary>Attaches one rendered executed PDF and finalizes its request and lease atomically.</summary>
public sealed record FinalizeNativeEsignRequestCommand(
    string PublicId,
    string StorageKey,
    string FileName,
    long FileSize,
    string ContentSha256,
    DateTime CompletedAtUtc) : IAtomicCommandData;

public sealed record FinalizeNativeEsignRequestResult(
    string PublicId,
    int SignatureRequestId,
    int LeaseId,
    int StoredFileId) : IAtomicResultData;
