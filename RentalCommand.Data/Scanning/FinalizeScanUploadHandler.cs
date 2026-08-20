using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;
using RentalCommand.Core.Documents;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Documents;

namespace RentalCommand.Data.Scanning;

public sealed class FinalizeScanUploadHandler
    : IAtomicCommandHandler<FinalizeScanUploadCommand, FinalizeScanUploadResult>
{
    public Task<FinalizeScanUploadResult> HandleAsync(
        FinalizeScanUploadCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        throw ScanDraftWriteSupport.RetiredPath();

    public Task AuthorizeReplayAsync(
        FinalizeScanUploadCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw ScanDraftWriteSupport.RetiredPath();

    public static async Task<FinalizeScanUploadResult> ExecuteAsync(
        RentalCommandDbContext db,
        FinalizeScanUploadCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.Files is not { Count: > 0 and <= 100 })
            throw new InvalidOperationException("A scan upload must contain between 1 and 100 files.");

        var captureContext = command.CaptureContext
            ?? throw new InvalidOperationException("A canonical scan capture context is required.");
        await AuthorizeCaptureContextAsync(command, captureContext, db, ct);

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
        var pendingRows = await AtomicPendingFileUploadPersistence.LockPreparedSetAsync(db,
            context, command.PortfolioId,
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
        var activeStatusNames = new[] { "Pending", "Processing", "Reviewing" };
        var sourceHashes = command.Files.Select(file => file.SourceSha256).Distinct().ToArray();
        var duplicateMatchesByHash = await db.Set<ScanDraft>()
            .Where(draft => draft.PortfolioId == command.PortfolioId
                && draft.TargetEntityType == command.TargetEntityType
                && draft.SourceContentSha256 != null
                && sourceHashes.Contains(draft.SourceContentSha256)
                && activeStatusNames.Contains(draft.Status)
                && draft.CaptureExperience == captureContext.Experience
                && draft.CaptureAccessContextId == captureContext.AccessContextId
                && draft.CaptureAccessRevision == captureContext.AccessRevision
                && draft.CapturePropertyId == captureContext.PropertyId
                && draft.CaptureUnitId == captureContext.UnitId
                && draft.CaptureLeaseManagementId == captureContext.LeaseManagementId
                && draft.CaptureLeaseAgreementId == captureContext.LeaseAgreementId
                && draft.CaptureTenantAccountId == captureContext.TenantAccountId
                && draft.CaptureTenantLedgerEntryId == captureContext.TenantLedgerEntryId
                && draft.CaptureWorkOrderId == captureContext.WorkOrderId
                && draft.CaptureApplicationId == captureContext.ApplicationId
                && draft.CaptureRentalListingId == captureContext.RentalListingId
                && draft.SourceStoredFileId != null
                && draft.SourceLabel == captureContext.SourceLabel)
            .GroupBy(draft => draft.SourceContentSha256!)
            .Select(group => group
                .OrderBy(draft => draft.Id)
                .Select(draft => new ScanUploadDuplicateMatch(
                    draft.SourceContentSha256!,
                    draft.Id,
                    draft.Status,
                    draft.FilePath,
                    draft.SourceStoredFileId!.Value))
                .First())
            .ToDictionaryAsync(match => match.SourceSha256, ct);
        var duplicateMatches = command.Files
            .Select(file => duplicateMatchesByHash.GetValueOrDefault(file.SourceSha256))
            .ToArray();

        ScanBatch? batch = null;
        if (command.CreateBatch && duplicateMatches.Any(match => match is null))
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
            db.Add(batch);
        }

        var sourceRows = new List<StoredFile?>(command.Files.Count);
        var thumbnailRows = new List<StoredFile?>(command.Files.Count);
        for (var index = 0; index < command.Files.Count; index++)
        {
            var file = command.Files[index];
            if (duplicateMatches[index] is not null)
            {
                sourceRows.Add(null);
                thumbnailRows.Add(null);
                continue;
            }

            var source = CreateStoredFile(command, file.SourceFileName, file.SourceStoragePath,
                file.SourceContentType, file.SourceSizeBytes);
            sourceRows.Add(source);
            db.Add(source);
            BindFileAudit(context, command, source, file.SourceSha256, "source", index);

            StoredFile? thumbnail = null;
            if (file.ThumbnailPendingUploadId.HasValue)
            {
                thumbnail = CreateStoredFile(command, file.ThumbnailFileName!, file.ThumbnailStoragePath!,
                    "image/jpeg", file.ThumbnailSizeBytes!.Value);
                db.Add(thumbnail);
                BindFileAudit(context, command, thumbnail, file.ThumbnailSha256!, "thumbnail", index);
            }
            thumbnailRows.Add(thumbnail);
        }

        // Database-generated file/batch ids are needed by ScanDraft and PendingFileUpload.
        if (sourceRows.Any(row => row is not null) || batch is not null)
        {
            await context.FlushBusinessAsync(ct);
        }
        if (batch is not null)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
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

        var drafts = new List<ScanDraft?>(command.Files.Count);
        for (var index = 0; index < command.Files.Count; index++)
        {
            if (duplicateMatches[index] is not null)
            {
                drafts.Add(null);
                continue;
            }

            var draft = new ScanDraft
            {
                PortfolioId = command.PortfolioId,
                BatchId = batch?.Id,
                FilePath = sourceRows[index]!.FilePath,
                SourceStoredFileId = sourceRows[index]!.Id,
                SourceContentSha256 = command.Files[index].SourceSha256,
                SourceLabel = captureContext.SourceLabel,
                CaptureExperience = captureContext.Experience,
                CaptureAccessContextId = captureContext.AccessContextId,
                CaptureAccessRevision = captureContext.AccessRevision,
                CapturePropertyId = captureContext.PropertyId,
                CaptureUnitId = captureContext.UnitId,
                CaptureLeaseManagementId = captureContext.LeaseManagementId,
                CaptureLeaseAgreementId = captureContext.LeaseAgreementId,
                CaptureTenantAccountId = captureContext.TenantAccountId,
                CaptureTenantLedgerEntryId = captureContext.TenantLedgerEntryId,
                CaptureWorkOrderId = captureContext.WorkOrderId,
                CaptureApplicationId = captureContext.ApplicationId,
                CaptureRentalListingId = captureContext.RentalListingId,
                ThumbnailPath = thumbnailRows[index]?.FilePath,
                TargetEntityType = command.TargetEntityType,
                Status = "Pending",
                CreatedAt = command.UploadedAtUtc,
            };
            drafts.Add(draft);
            db.Add(draft);
            context.BindSemanticAudit(draft, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(ScanDraft),
                0,
                AuditLogOperation.Created,
                UserId: command.UploadedByUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    command.TargetEntityType,
                    BatchId = batch?.Id,
                    SourceStoredFileId = sourceRows[index]!.Id,
                    CaptureContext = captureContext,
                    command.ClientOperationId,
                }),
                ChangeReason: "Scan upload admitted for extraction"));
        }
        if (drafts.Any(draft => draft is not null))
        {
            await context.FlushBusinessAsync(ct);
        }

        for (var index = 0; index < command.Files.Count; index++)
        {
            var sourcePending = pendingById[command.Files[index].SourcePendingUploadId];
            if (duplicateMatches[index] is { } duplicate)
            {
                FinalizePending(sourcePending, duplicate.SourceStoredFileId, command.UploadedAtUtc);
                StageDuplicateBlobCleanup(
                    context, command, sourcePending.Id, sourcePending.StoragePath,
                    duplicate.SourceStoredFileId);

                if (command.Files[index].ThumbnailPendingUploadId is Guid duplicateThumbnailPendingId)
                {
                    var thumbnailPending = pendingById[duplicateThumbnailPendingId];
                    FinalizeDuplicatePending(thumbnailPending, command.UploadedAtUtc);
                    StageDuplicateBlobCleanup(context, command, thumbnailPending.Id, thumbnailPending.StoragePath);
                }

                continue;
            }

            var draft = drafts[index]!;
            var source = sourceRows[index]!;
            FinalizePending(sourcePending, source.Id, command.UploadedAtUtc);
            LinkFileToDraft(context, command, source, draft.Id);

            if (thumbnailRows[index] is { } thumbnail
                && command.Files[index].ThumbnailPendingUploadId is Guid thumbnailPendingId)
            {
                FinalizePending(pendingById[thumbnailPendingId], thumbnail.Id, command.UploadedAtUtc);
                LinkFileToDraft(context, command, thumbnail, draft.Id);
            }

            // Realtime invalidation is a durable destination: it must not be emitted before commit.
            context.StageOutbox(new OutboxMessage
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
            drafts.Select((draft, index) => duplicateMatches[index] is { } duplicate
                    ? new FinalizedScanDraft(duplicate.DraftId, duplicate.Status, duplicate.FilePath)
                    : new FinalizedScanDraft(draft!.Id, draft.Status, draft.FilePath))
                .ToArray());
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext db,
        FinalizeScanUploadCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var captureContext = command.CaptureContext
            ?? throw new InvalidOperationException("A canonical scan capture context is required.");
        return AuthorizeCaptureContextAsync(command, captureContext, db, ct);
    }

    private static async Task AuthorizeCaptureContextAsync(
        FinalizeScanUploadCommand command,
        ScanCaptureContextData context,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || command.UploadedByUserId <= 0
            || context.AccessContextId != command.AccessContextId
            || context.AccessRevision != command.ExpectedAccessRevision)
            throw Unauthorized();

        var securityNowUtc = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var valid = await CaptureContextAuthorizationQuery(command, context, db, securityNowUtc)
            .SingleOrDefaultAsync(ct);

        if (!valid)
            throw Unauthorized();
    }

    internal static IQueryable<bool> CaptureContextAuthorizationQuery(
        FinalizeScanUploadCommand command,
        ScanCaptureContextData context,
        RentalCommandDbContext db,
        DateTime securityNowUtc)
    {
        var capabilities = ScanDraftAuthorizationQuery.CapabilitiesForTarget(command.TargetEntityType);
        if (capabilities.Count == 0)
            throw Unauthorized();

        var capabilityKeys = capabilities.Distinct(StringComparer.Ordinal).ToArray();
        var scope = new WorkspaceReadScope(
            command.PortfolioId,
            command.UploadedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision);
        var propertyAssignments = db.AuthorizedAssignmentsForScope(
            scope,
            capabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            securityNowUtc);
        var supportsAssignedWork = capabilityKeys.Contains(
            CapabilityKeys.AssignedWorkUpdate,
            StringComparer.Ordinal);
        var workOrderAssignments = supportsAssignedWork
            ? db.AuthorizedAssignmentsForScope(
                scope,
                [CapabilityKeys.AssignedWorkUpdate],
                CapabilityAuthorizationTargetKind.WorkOrder,
                securityNowUtc)
            : db.Set<MembershipRoleAssignment>().Where(_ => false);

        var authorizedWorkOrders = db.Set<WorkOrder>().Where(workOrder =>
            workOrder.PortfolioId == command.PortfolioId &&
            (propertyAssignments.Any(assignment =>
                (assignment.RoleProfile!.Capabilities.Any(item =>
                     item.CapabilityDefinition!.Key == CapabilityKeys.WorkManage) &&
                 (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                  assignment.SelectedProperties.Any(selected => selected.PortfolioId == command.PortfolioId &&
                                                               selected.PropertyId == workOrder.PropertyId)))) ||
             workOrderAssignments.Any(assignment =>
                assignment.RoleProfile!.Capabilities.Any(item =>
                     item.CapabilityDefinition!.Key == CapabilityKeys.AssignedWorkUpdate) &&
                 assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                 db.Set<WorkOrderResponsibility>().Any(responsibility =>
                     responsibility.WorkOrderId == workOrder.Id &&
                     responsibility.PortfolioId == command.PortfolioId &&
                     responsibility.WorkspaceMembershipId == assignment.WorkspaceMembershipId &&
                     responsibility.MembershipRoleAssignmentId == assignment.Id &&
                     responsibility.EffectiveFromUtc <= securityNowUtc &&
                     (responsibility.EffectiveToUtc == null || responsibility.EffectiveToUtc > securityNowUtc)))));

        var properties = db.Set<Property>();
        var authorizedProperties = properties.Where(property =>
            property.PortfolioId == command.PortfolioId && property.DeletedAt == null
            && (propertyAssignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == command.PortfolioId
                        && selected.PropertyId == property.Id)))
                || context.WorkOrderId != null && authorizedWorkOrders.Any(workOrder =>
                    workOrder.Id == context.WorkOrderId && workOrder.PropertyId == property.Id)));
        var units = db.Set<Unit>();
        var relationships = db.Set<LeaseManagement>();
        var agreements = db.Set<LeaseAgreement>();
        var accounts = db.Set<TenantAccount>();
        var ledgerEntries = db.Set<TenantLedgerEntry>();
        var workOrders = db.Set<WorkOrder>();
        var applications = db.Set<RentalApplication>();
        var listings = db.Set<RentalListing>();

        // One translated predicate validates current authority and the complete capture graph before
        // any StoredFile, ScanBatch, or ScanDraft row is added. Every nested Any becomes a correlated
        // EXISTS in this SQL statement; no candidate business rows are materialized in memory.
        return db.Set<Portfolio>()
            .Where(portfolio => portfolio.Id == command.PortfolioId)
            .Select(_ =>
                (propertyAssignments.Any() || workOrderAssignments.Any())
                // Assigned technicians must bind WorkOrder scans to a current responsibility.
                // Other targets already select their own required capability above, so a leasing
                // agent must not also need unrelated work-management authority to finish a lease
                // or application scan.
                && (command.TargetEntityType != "WorkOrder"
                    || context.WorkOrderId != null
                    || propertyAssignments.Any())
                && (context.PropertyId == null || authorizedProperties.Any(property =>
                    property.Id == context.PropertyId.Value))
                && (context.UnitId == null || units.Any(unit =>
                    unit.Id == context.UnitId.Value
                    && unit.PortfolioId == command.PortfolioId
                    && authorizedProperties.Any(property => property.Id == unit.PropertyId)
                    && (context.PropertyId == null || unit.PropertyId == context.PropertyId.Value)))
                && (context.LeaseManagementId == null || relationships.Any(relationship =>
                    relationship.Id == context.LeaseManagementId.Value
                    && relationship.PortfolioId == command.PortfolioId
                    && authorizedProperties.Any(property => property.Id == relationship.PropertyId)
                    && (context.PropertyId == null || relationship.PropertyId == context.PropertyId.Value)
                    && (context.UnitId == null || relationship.UnitId == context.UnitId.Value)))
                && (context.LeaseAgreementId == null || agreements.Any(agreement =>
                    agreement.Id == context.LeaseAgreementId.Value
                    && agreement.PortfolioId == command.PortfolioId
                    && agreement.LeaseManagement != null
                    && authorizedProperties.Any(property =>
                        property.Id == agreement.LeaseManagement.PropertyId)
                    && (context.LeaseManagementId == null
                        || agreement.LeaseManagementId == context.LeaseManagementId.Value)
                    && (context.PropertyId == null
                        || agreement.LeaseManagement!.PropertyId == context.PropertyId.Value)
                    && (context.UnitId == null
                        || agreement.LeaseManagement!.UnitId == context.UnitId.Value)))
                && (context.TenantAccountId == null || accounts.Any(account =>
                    account.Id == context.TenantAccountId.Value
                    && account.PortfolioId == command.PortfolioId
                    && account.LeaseManagement != null
                    && authorizedProperties.Any(property =>
                        property.Id == account.LeaseManagement.PropertyId)
                    && (context.LeaseManagementId == null
                        || account.LeaseManagementId == context.LeaseManagementId.Value)
                    && (context.LeaseAgreementId == null || agreements.Any(agreement =>
                        agreement.Id == context.LeaseAgreementId.Value
                        && agreement.LeaseManagementId == account.LeaseManagementId))
                    && (context.PropertyId == null
                        || account.LeaseManagement!.PropertyId == context.PropertyId.Value)
                    && (context.UnitId == null
                        || account.LeaseManagement!.UnitId == context.UnitId.Value)))
                && (context.TenantLedgerEntryId == null || ledgerEntries.Any(entry =>
                    entry.Id == context.TenantLedgerEntryId.Value
                    && entry.PortfolioId == command.PortfolioId
                    && entry.TenantAccount != null && entry.TenantAccount.LeaseManagement != null
                    && authorizedProperties.Any(property =>
                        property.Id == entry.TenantAccount.LeaseManagement.PropertyId)
                    && (context.TenantAccountId == null
                        || entry.TenantAccountId == context.TenantAccountId.Value)
                    && (context.LeaseManagementId == null
                        || entry.TenantAccount!.LeaseManagementId == context.LeaseManagementId.Value)
                    && (context.LeaseAgreementId == null
                        || entry.LeaseAgreementId == context.LeaseAgreementId.Value)
                    && (context.PropertyId == null
                        || entry.TenantAccount!.LeaseManagement!.PropertyId == context.PropertyId.Value)
                    && (context.UnitId == null
                        || entry.TenantAccount!.LeaseManagement!.UnitId == context.UnitId.Value)))
                && (context.WorkOrderId == null || authorizedWorkOrders.Any(workOrder =>
                    workOrder.Id == context.WorkOrderId.Value
                    && workOrder.PortfolioId == command.PortfolioId
                    && (context.PropertyId == null || workOrder.PropertyId == context.PropertyId.Value)
                    && (context.UnitId == null || workOrder.UnitId == context.UnitId.Value)
                    && (context.LeaseManagementId == null
                        || workOrder.LeaseManagementId == context.LeaseManagementId.Value)
                    && (context.RentalListingId == null || listings.Any(listing =>
                        listing.Id == context.RentalListingId.Value
                        && listing.PropertyId == workOrder.PropertyId
                        && listing.UnitId == workOrder.UnitId))
                    && (context.ApplicationId == null || applications.Any(application =>
                        application.Id == context.ApplicationId.Value
                        && application.PropertyId == workOrder.PropertyId
                        && application.UnitId == workOrder.UnitId))))
                && (context.ApplicationId == null || applications.Any(application =>
                    application.Id == context.ApplicationId.Value
                    && application.PortfolioId == command.PortfolioId
                    && application.PropertyId != null
                    && authorizedProperties.Any(property => property.Id == application.PropertyId)
                    && (context.PropertyId == null || application.PropertyId == context.PropertyId.Value)
                    && (context.UnitId == null || application.UnitId == context.UnitId.Value)
                    && (context.LeaseManagementId == null
                        || application.PreparedLeaseManagementId == context.LeaseManagementId.Value)
                    && (context.RentalListingId == null || listings.Any(listing =>
                        listing.Id == context.RentalListingId.Value
                        && listing.PropertyId == application.PropertyId
                        && listing.UnitId == application.UnitId))))
                && (context.RentalListingId == null || listings.Any(listing =>
                    listing.Id == context.RentalListingId.Value
                    && listing.PortfolioId == command.PortfolioId
                    && authorizedProperties.Any(property => property.Id == listing.PropertyId)
                    && (context.PropertyId == null || listing.PropertyId == context.PropertyId.Value)
                    && (context.UnitId == null || listing.UnitId == context.UnitId.Value)
                    && (context.LeaseManagementId == null || relationships.Any(relationship =>
                        relationship.Id == context.LeaseManagementId.Value
                        && relationship.PropertyId == listing.PropertyId
                        && relationship.UnitId == listing.UnitId)))));
    }

    private static UnauthorizedAccessException Unauthorized() =>
        new("The current session is not authorized to finalize this scan upload.");

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
        IAtomicCommandContext context,
        FinalizeScanUploadCommand command,
        StoredFile row,
        string contentSha256,
        string role,
        int ordinal) => context.BindSemanticAudit(row, new AtomicSemanticAudit(
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
        IAtomicCommandContext context,
        FinalizeScanUploadCommand command,
        StoredFile row,
        int draftId)
    {
        row.EntityId = draftId;
        context.BindSemanticAudit(row, new AtomicSemanticAudit(
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

    private static void FinalizeDuplicatePending(PendingFileUpload upload, DateTime nowUtc)
    {
        upload.State = PendingFileUploadState.Finalized;
        upload.StoredFileId = null;
        upload.UpdatedAtUtc = nowUtc;
    }

    private static void StageDuplicateBlobCleanup(
        IAtomicCommandContext context,
        FinalizeScanUploadCommand command,
        Guid pendingUploadId,
        string storagePath,
        int? storedFileId = null)
    {
        var payload = storedFileId is int id
            ? JsonSerializer.Serialize(new { pendingUploadId, storedFileId = id, storagePath })
            : JsonSerializer.Serialize(new { pendingUploadId, storagePath });
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "blob-delete",
            Payload = payload,
            IdempotencyKey = $"scan-upload-duplicate-blob:{pendingUploadId}",
            CreatedAtUtc = command.UploadedAtUtc,
            NextAttemptAtUtc = command.UploadedAtUtc,
        });
    }

    private sealed record ScanUploadDuplicateMatch(
        string SourceSha256,
        int DraftId,
        string Status,
        string FilePath,
        int SourceStoredFileId);

}
