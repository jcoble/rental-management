using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Imaging;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Core.Time;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Scanning;

namespace RentalCommand.Api.Scanning;

public sealed class ScanUploadService : IScanUploadService
{
    private readonly IRequestWriteExecutor _writes;
    private readonly RentalCommand.Data.RentalCommandDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IPendingFileUploadStore _pendingUploads;
    private readonly UploadSettings _settings;
    private readonly TimeProvider _timeProvider;

    public ScanUploadService(
        RentalCommand.Data.RentalCommandDbContext db,
        IRequestWriteExecutor writes,
        IFileStorage storage,
        IPendingFileUploadStore pendingUploads,
        IOptions<UploadSettings> settings,
        TimeProvider timeProvider)
    {
        _db = db;
        _writes = writes;
        _storage = storage;
        _pendingUploads = pendingUploads;
        _settings = settings.Value;
        _timeProvider = timeProvider;
    }

    public async Task<FinalizeScanUploadResult> UploadAsync(
        WorkspaceReadScope scope,
        string clientOperationId,
        string targetEntityType,
        bool createBatch,
        string? batchName,
        ScanCaptureContextData captureContext,
        IReadOnlyList<ScanUploadFilePayload> files,
        CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var userId = scope.UserId;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        if (scope.SessionId == Guid.Empty || scope.AccessContextId <= 0 || scope.AccessRevision <= 0)
            throw new ArgumentException("An active workspace session is required.", nameof(scope));
        if (captureContext.AccessContextId != scope.AccessContextId
            || captureContext.AccessRevision != scope.AccessRevision)
            throw new UnauthorizedAccessException(
                "The scan capture context does not match the active workspace access context.");
        var operationId = NormalizeOperationId(clientOperationId);
        if (files is not { Count: > 0 and <= 100 })
            throw new ArgumentException("A scan upload must contain between 1 and 100 files.", nameof(files));

        var preparedFiles = new List<PreparedFile>(files.Count);
        foreach (var file in files)
        {
            var safeName = DiskFileStorage.SanitizeFileName(file.FileName);
            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType.Trim().ToLowerInvariant();
            var header = file.Bytes.Length > 0 ? file.Bytes[..Math.Min(16, file.Bytes.Length)] : null;
            var (valid, error) = FileUploadValidator.ValidateScanUpload(
                safeName, contentType, file.Bytes.Length, _settings, header);
            if (!valid) throw new ArgumentException(error ?? "Invalid scan upload.", nameof(files));

            var thumbnailBytes = ThumbnailResizer.ResizeToJpeg(file.Bytes, maxDim: 1000, quality: 72);
            preparedFiles.Add(new PreparedFile(
                file.Bytes,
                safeName,
                contentType,
                Sha256(file.Bytes),
                thumbnailBytes,
                thumbnailBytes is null ? null : Sha256(thumbnailBytes)));
        }

        var normalizedTarget = targetEntityType?.Trim() ?? string.Empty;
        var normalizedBatchName = string.IsNullOrWhiteSpace(batchName) ? null : batchName.Trim();
        var fingerprint = RequestFingerprint(
            normalizedTarget, createBatch, normalizedBatchName, captureContext, preparedFiles);
        var now = _timeProvider.UtcNow();

        // Reserve every deterministic object key before the first external storage call. If any later
        // upload fails, this complete admission set remains retryable and scavengable.
        var admitted = new List<AdmittedFile>(preparedFiles.Count);
        for (var index = 0; index < preparedFiles.Count; index++)
        {
            var file = preparedFiles[index];
            var source = await _pendingUploads.PrepareAsync(
                portfolioId,
                userId,
                "scan-source",
                $"{operationId}:{index}:source",
                fingerprint,
                file.FileName,
                file.ContentType,
                file.Bytes.LongLength,
                now,
                ct);

            PendingFileUploadAdmission? thumbnail = null;
            if (file.ThumbnailBytes is not null)
            {
                thumbnail = await _pendingUploads.PrepareAsync(
                    portfolioId,
                    userId,
                    "scan-thumbnail",
                    $"{operationId}:{index}:thumbnail",
                    fingerprint,
                    $"{Path.GetFileNameWithoutExtension(file.FileName)}-preview.jpg",
                    "image/jpeg",
                    file.ThumbnailBytes.LongLength,
                    now,
                    ct);
            }
            admitted.Add(new AdmittedFile(file, source, thumbnail));
        }

        var states = admitted
            .SelectMany(file => file.Thumbnail is null
                ? new[] { file.Source.State }
                : new[] { file.Source.State, file.Thumbnail.State })
            .Distinct()
            .ToArray();
        if (states.Any(state => state == PendingFileUploadState.Abandoned)
            || states.Length > 1)
        {
            throw new InvalidOperationException(
                "The scan upload admission set is inconsistent and cannot be finalized.");
        }

        if (states.Single() == PendingFileUploadState.Prepared)
        {
            foreach (var file in admitted)
            {
                await _storage.UploadAtAsync(
                    new MemoryStream(file.File.Bytes, writable: false),
                    file.Source.StoragePath,
                    file.File.FileName,
                    file.File.ContentType,
                    ct);
                if (file.Thumbnail is not null && file.File.ThumbnailBytes is not null)
                {
                    await _storage.UploadAtAsync(
                        new MemoryStream(file.File.ThumbnailBytes, writable: false),
                        file.Thumbnail.StoragePath,
                        $"{Path.GetFileNameWithoutExtension(file.File.FileName)}-preview.jpg",
                        "image/jpeg",
                        ct);
                }
            }
        }

        var commandFiles = admitted.Select(file => new FinalizeScanUploadFile(
            file.Source.Id,
            file.Source.StoragePath,
            file.File.FileName,
            file.File.ContentType,
            file.File.Bytes.LongLength,
            file.File.Sha256,
            file.Thumbnail?.Id,
            file.Thumbnail?.StoragePath,
            file.Thumbnail is null ? null : $"{Path.GetFileNameWithoutExtension(file.File.FileName)}-preview.jpg",
            file.File.ThumbnailBytes?.LongLength,
            file.File.ThumbnailSha256)).ToArray();

        var command = new FinalizeScanUploadCommand(
                portfolioId,
                userId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                operationId,
                fingerprint,
                normalizedTarget,
                createBatch,
                normalizedBatchName,
                now,
                commandFiles,
                captureContext);
        var outcome = await _writes.ExecuteAsync(
            $"{portfolioId}:{userId}:{Digest(operationId)}",
            ScanDraftWriteSupport.Write(
                "scan-upload.finalize", command, ScanDraftWriteSupport.FinalizeResultContract,
                (request, context, token) => FinalizeScanUploadHandler.ExecuteAsync(
                    _db, request, context, token),
                (request, context, token) => FinalizeScanUploadHandler.AuthorizeAsync(
                    _db, request, context, token)),
            ct);
        return outcome.Value;
    }

    private static string NormalizeOperationId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > 160)
            throw new ArgumentOutOfRangeException(nameof(value), "A request key cannot exceed 160 characters.");
        return normalized;
    }

    private static string RequestFingerprint(
        string targetEntityType,
        bool createBatch,
        string? batchName,
        ScanCaptureContextData captureContext,
        IReadOnlyList<PreparedFile> files) => Digest(JsonSerializer.Serialize(new
        {
            targetEntityType,
            createBatch,
            batchName,
            captureContext,
            files = files.Select((file, ordinal) => new
            {
                ordinal,
                file.FileName,
                file.ContentType,
                sizeBytes = file.Bytes.LongLength,
                file.Sha256,
                thumbnailSizeBytes = file.ThumbnailBytes?.LongLength,
                file.ThumbnailSha256,
            }),
        }));

    private static string Sha256(byte[] bytes) => Convert.ToHexString(
        SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record PreparedFile(
        byte[] Bytes,
        string FileName,
        string ContentType,
        string Sha256,
        byte[]? ThumbnailBytes,
        string? ThumbnailSha256);

    private sealed record AdmittedFile(
        PreparedFile File,
        PendingFileUploadAdmission Source,
        PendingFileUploadAdmission? Thumbnail);
}
