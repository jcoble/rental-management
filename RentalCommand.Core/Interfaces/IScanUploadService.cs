using RentalCommand.Core.Authorization;
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
        WorkspaceReadScope scope,
        string clientOperationId,
        string targetEntityType,
        bool createBatch,
        string? batchName,
        ScanCaptureContextData captureContext,
        IReadOnlyList<ScanUploadFilePayload> files,
        CancellationToken ct = default);
}
