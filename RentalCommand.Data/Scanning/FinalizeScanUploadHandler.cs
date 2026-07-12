using System.Text.Json;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;
using RentalCommand.Data.Documents;

namespace RentalCommand.Data.Scanning;

public sealed class FinalizeScanUploadHandler
    : IAtomicCommandHandler<FinalizeScanUploadCommand, FinalizeScanUploadResult>
{
    public async Task<FinalizeScanUploadResult> HandleAsync(
        FinalizeScanUploadCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.Files is not { Count: > 0 and <= 100 })
            throw new InvalidOperationException("A scan upload must contain between 1 and 100 files.");

        var expectations = new List<AtomicPendingFileUploadExpectation>(command.Files.Count * 2);
        for (var index = 0; index < command.Files.Count; index++)
        {
            var file = command.Files[index];
            expectations.Add(new AtomicPendingFileUploadExpectation(
                file.SourcePendingUploadId,
                "scan-source",
                PendingFileUploadStore.ComputeOperationKeyHash(
                    $"{command.ClientOperationId}:{index}:source"),
                command.RequestFingerprint,
                file.SourceStoragePath,
                file.SourceFileName,
                file.SourceContentType,
                file.SourceSizeBytes));

            if (file.ThumbnailPendingUploadId is Guid thumbnailId)
            {
                if (string.IsNullOrWhiteSpace(file.ThumbnailStoragePath)
                    || string.IsNullOrWhiteSpace(file.ThumbnailFileName)
                    || file.ThumbnailSizeBytes is null or < 0
                    || string.IsNullOrWhiteSpace(file.ThumbnailSha256))
                {
                    throw new InvalidOperationException("A scan thumbnail admission is incomplete.");
                }
                expectations.Add(new AtomicPendingFileUploadExpectation(
                    thumbnailId,
                    "scan-thumbnail",
                    PendingFileUploadStore.ComputeOperationKeyHash(
                        $"{command.ClientOperationId}:{index}:thumbnail"),
                    command.RequestFingerprint,
                    file.ThumbnailStoragePath,
                    file.ThumbnailFileName,
                    "image/jpeg",
                    file.ThumbnailSizeBytes.Value));
            }
            else if (file.ThumbnailStoragePath is not null
                || file.ThumbnailFileName is not null
                || file.ThumbnailSizeBytes is not null
                || file.ThumbnailSha256 is not null)
            {
                throw new InvalidOperationException("Scan thumbnail metadata cannot exist without an admission id.");
            }
        }

        var pendingIds = expectations.Select(expectation => expectation.Id).ToArray();
        if (pendingIds.Distinct().Count() != pendingIds.Length)
            throw new InvalidOperationException("A scan upload cannot reuse one pending blob admission twice.");

        // One PostgreSQL statement matches every security/integrity field and locks the complete
        // set in UUID order. Cleanup uses FOR UPDATE SKIP LOCKED, so it cannot claim or abandon any
        // admission while this transaction is finalizing the set.
        var pendingRows = await attempt.PendingFileUploads.LockPreparedSetAsync(
            command.PortfolioId,
            command.UploadedByUserId,
            expectations,
            ct);
        // The SQL itself gates on its DB-side matched count. Reassert the bounded command cardinality
        // after PostgreSQL's row-lock recheck as a concurrency fence; this is not business-data shaping.
        if (pendingRows.Count != expectations.Count)
        {
            throw new InvalidOperationException(
                "The complete scan upload admission set is unavailable or does not match its actor, purpose, operation, or blob metadata.");
        }

        var pendingById = pendingRows.ToDictionary(upload => upload.Id);

        ScanBatch? batch = null;
        if (command.CreateBatch)
        {
            batch = new ScanBatch
            {
                PortfolioId = command.PortfolioId,
                Name = command.BatchName,
                TargetEntityType = command.TargetEntityType,
                Status = ScanBatchStatus.Processing,
                FileCount = command.Files.Count,
                CreatedAtUtc = command.UploadedAtUtc,
            };
            attempt.Persistence.Add(batch);
        }

        var sourceRows = new List<StoredFile>(command.Files.Count);
        var thumbnailRows = new List<StoredFile?>(command.Files.Count);
        for (var index = 0; index < command.Files.Count; index++)
        {
            var file = command.Files[index];
            var source = CreateStoredFile(command, file.SourceFileName, file.SourceStoragePath,
                file.SourceContentType, file.SourceSizeBytes);
            sourceRows.Add(source);
            attempt.Persistence.Add(source);
            BindFileAudit(attempt, command, source, file.SourceSha256, "source", index);

            StoredFile? thumbnail = null;
            if (file.ThumbnailPendingUploadId.HasValue)
            {
                thumbnail = CreateStoredFile(command, file.ThumbnailFileName!, file.ThumbnailStoragePath!,
                    "image/jpeg", file.ThumbnailSizeBytes!.Value);
                attempt.Persistence.Add(thumbnail);
                BindFileAudit(attempt, command, thumbnail, file.ThumbnailSha256!, "thumbnail", index);
            }
            thumbnailRows.Add(thumbnail);
        }

        // Database-generated file/batch ids are needed by ScanDraft and PendingFileUpload.
        await attempt.FlushBusinessAsync(ct);
        if (batch is not null)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(ScanBatch),
                batch.Id,
                AuditLogOperation.Created,
                UserId: command.UploadedByUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    batch.Name,
                    batch.TargetEntityType,
                    batch.FileCount,
                    command.ClientOperationId,
                }),
                ChangeReason: "Scan batch admitted"));
        }

        var captureContext = command.CaptureContext
            ?? new ScanCaptureContextData(null, null, null, null, null, null, null, null, null, null);
        var drafts = new List<ScanDraft>(command.Files.Count);
        for (var index = 0; index < command.Files.Count; index++)
        {
            var draft = new ScanDraft
            {
                PortfolioId = command.PortfolioId,
                BatchId = batch?.Id,
                FilePath = sourceRows[index].FilePath,
                SourceStoredFileId = sourceRows[index].Id,
                SourceContentSha256 = command.Files[index].SourceSha256,
                SourceLabel = captureContext.SourceLabel,
                CaptureExperience = captureContext.Experience,
                CaptureAccessContextId = captureContext.AccessContextId,
                CaptureAccessRevision = captureContext.AccessRevision,
                CapturePropertyId = captureContext.PropertyId,
                CaptureUnitId = captureContext.UnitId,
                CaptureLeaseManagementId = captureContext.LeaseManagementId,
                CaptureTenantAccountId = captureContext.TenantAccountId,
                CaptureFocusedRecordKind = captureContext.FocusedRecordKind,
                CaptureFocusedRecordId = captureContext.FocusedRecordId,
                ThumbnailPath = thumbnailRows[index]?.FilePath,
                TargetEntityType = command.TargetEntityType,
                Status = "Pending",
                CreatedAt = command.UploadedAtUtc,
            };
            drafts.Add(draft);
            attempt.Persistence.Add(draft);
            attempt.BindSemanticAudit(draft, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(ScanDraft),
                0,
                AuditLogOperation.Created,
                UserId: command.UploadedByUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    command.TargetEntityType,
                    BatchId = batch?.Id,
                    SourceStoredFileId = sourceRows[index].Id,
                    CaptureContext = captureContext,
                    command.ClientOperationId,
                }),
                ChangeReason: "Scan upload admitted for extraction"));
        }
        await attempt.FlushBusinessAsync(ct);

        for (var index = 0; index < command.Files.Count; index++)
        {
            var draft = drafts[index];
            var source = sourceRows[index];
            var sourcePending = pendingById[command.Files[index].SourcePendingUploadId];
            FinalizePending(sourcePending, source.Id, command.UploadedAtUtc);
            LinkFileToDraft(attempt, command, source, draft.Id);

            if (thumbnailRows[index] is { } thumbnail
                && command.Files[index].ThumbnailPendingUploadId is Guid thumbnailPendingId)
            {
                FinalizePending(pendingById[thumbnailPendingId], thumbnail.Id, command.UploadedAtUtc);
                LinkFileToDraft(attempt, command, thumbnail, draft.Id);
            }

            // Realtime invalidation is a durable destination: it must not be emitted before commit.
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(ScanDraft),
                    entityId = draft.Id,
                    data = new { status = draft.Status, batchId = draft.BatchId },
                }),
                IdempotencyKey = $"scan-draft-created:{draft.Id}",
                CreatedAtUtc = command.UploadedAtUtc,
                NextAttemptAtUtc = command.UploadedAtUtc,
            });
        }

        return new FinalizeScanUploadResult(
            batch?.Id,
            batch?.Name,
            command.TargetEntityType,
            drafts.Select(draft => new FinalizedScanDraft(draft.Id, draft.Status, draft.FilePath)).ToArray());
    }

    private static StoredFile CreateStoredFile(
        FinalizeScanUploadCommand command,
        string fileName,
        string storagePath,
        string contentType,
        long sizeBytes) => new()
        {
            PortfolioId = command.PortfolioId,
            FileName = fileName,
            FilePath = storagePath,
            ContentType = contentType,
            FileSize = sizeBytes,
            EntityType = nameof(ScanDraft),
            UploadedAt = command.UploadedAtUtc,
        };

    private static void BindFileAudit(
        IAtomicWriteAttempt attempt,
        FinalizeScanUploadCommand command,
        StoredFile row,
        string contentSha256,
        string role,
        int ordinal) => attempt.BindSemanticAudit(row, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(StoredFile),
            0,
            AuditLogOperation.Created,
            UserId: command.UploadedByUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                row.FileName,
                row.ContentType,
                row.FileSize,
                contentSha256,
                role,
                ordinal,
                command.ClientOperationId,
            }),
            ChangeReason: $"Scan {role} uploaded"));

    private static void LinkFileToDraft(
        IAtomicWriteAttempt attempt,
        FinalizeScanUploadCommand command,
        StoredFile row,
        int draftId)
    {
        row.EntityId = draftId;
        attempt.BindSemanticAudit(row, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(StoredFile),
            row.Id,
            AuditLogOperation.Updated,
            UserId: command.UploadedByUserId,
            NewValues: JsonSerializer.Serialize(new { row.EntityType, row.EntityId }),
            ChangeReason: "Scan file linked to draft"));
    }

    private static void FinalizePending(PendingFileUpload upload, int storedFileId, DateTime nowUtc)
    {
        upload.State = PendingFileUploadState.Finalized;
        upload.StoredFileId = storedFileId;
        upload.UpdatedAtUtc = nowUtc;
    }

}
