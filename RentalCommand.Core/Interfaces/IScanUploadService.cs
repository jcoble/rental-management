using RentalCommand.Core.Scanning;

namespace RentalCommand.Core.Interfaces;

public sealed record ScanUploadFilePayload(
    byte[] Bytes,
    string FileName,
    string ContentType);

/// <summary>Durably admits blobs and atomically creates reviewable scan drafts.</summary>
public interface IScanUploadService
{
    Task<FinalizeScanUploadResult> UploadAsync(
        int portfolioId,
        int userId,
        string clientOperationId,
        string targetEntityType,
        bool createBatch,
        string? batchName,
        IReadOnlyList<ScanUploadFilePayload> files,
        CancellationToken ct = default);

    Task<FinalizeScanUploadResult> UploadAsync(
        int portfolioId,
        int userId,
        string clientOperationId,
        string targetEntityType,
        bool createBatch,
        string? batchName,
        ScanCaptureContextData captureContext,
        IReadOnlyList<ScanUploadFilePayload> files,
        CancellationToken ct = default) =>
        UploadAsync(portfolioId, userId, clientOperationId, targetEntityType, createBatch,
            batchName, files, ct);
}
