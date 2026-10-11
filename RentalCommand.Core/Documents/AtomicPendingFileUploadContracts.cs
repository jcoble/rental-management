namespace RentalCommand.Core.Documents;

public sealed record AtomicPendingFileUploadExpectation(
    Guid Id,
    string Purpose,
    string OperationKeyHash,
    string RequestFingerprint,
    string StoragePath,
    string FileName,
    string ContentType,
    long SizeBytes);
