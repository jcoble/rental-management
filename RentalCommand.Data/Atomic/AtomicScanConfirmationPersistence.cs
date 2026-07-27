using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;
using RentalCommand.Core.Authorization;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Atomic;

/// <summary>Kernel-owned, EF-backed implementation of the restricted scan confirmation boundary.</summary>
internal sealed class AtomicScanConfirmationPersistence : IAtomicScanConfirmationPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;
    private readonly IAtomicLockingPersistence _locking;

    public AtomicScanConfirmationPersistence(
        RentalCommandDbContext db,
        AtomicAuditScope auditScope,
        IAtomicLockingPersistence locking)
    {
        _db = db;
        _auditScope = auditScope;
        _locking = locking;
    }

    public Task<bool> CanCreateAuthorizedAsync(
        WorkspaceReadScope scope,
        string? targetEntityType,
        int? propertyId,
        DateTime utcNow,
        CancellationToken ct = default) =>
        ScanDraftAuthorizationQuery.CanCreateDraftAsync(
            _db, scope, targetEntityType, propertyId, utcNow, ct);

    public async Task<AtomicScanDraftClaim> TryClaimAsync(
        int portfolioId,
        int draftId,
        string expectedTargetEntityType,
        string expectedDraftFingerprint,
        int confirmedByUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTargetEntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDraftFingerprint);
        await _locking.AcquireAsync(AtomicLockResource.ScanDraft, draftId, ct);

        var draft = await _db.ScanDrafts.SingleOrDefaultAsync(
            candidate => candidate.Id == draftId && candidate.PortfolioId == portfolioId,
            ct);
        if (draft is null)
        {
            return Empty(AtomicScanDraftClaimOutcome.NotFound, portfolioId, draftId);
        }

        if (draft.Status == "Confirmed")
        {
            if (draft.ConfirmedEntityId is not > 0)
            {
                throw new AtomicReceiptInvariantException(
                    $"Confirmed scan draft #{draftId} has no canonical target id.");
            }
            await ValidateSourceFileAsync(draft, ct);
            return new AtomicScanDraftClaim(
                AtomicScanDraftClaimOutcome.AlreadyConfirmed,
                portfolioId,
                draftId,
                draft.TargetEntityType,
                draft.SourceStoredFileId,
                draft.ExtractedFields,
                draft.SourceLabel,
                draft.TargetEntityType,
                draft.ConfirmedEntityId);
        }

        if (draft.Status == "Rejected")
        {
            return Snapshot(AtomicScanDraftClaimOutcome.Rejected, draft);
        }

        if (draft.Status != "Reviewing")
        {
            return Snapshot(AtomicScanDraftClaimOutcome.NotReady, draft);
        }

        await ValidateCaptureContextAsync(draft, ct);

        var currentFingerprint = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType,
            draft.SourceStoredFileId,
            draft.ExtractedFields,
            draft.SourceContentSha256,
            draft.CaptureAccessContextId,
            draft.CaptureAccessRevision,
            draft.CapturePropertyId,
            draft.CaptureUnitId,
            draft.CaptureLeaseManagementId,
            draft.CaptureLeaseAgreementId,
            draft.CaptureTenantAccountId,
            draft.CaptureTenantLedgerEntryId,
            draft.CaptureWorkOrderId,
            draft.CaptureApplicationId,
            draft.CaptureRentalListingId,
            draft.SourceLabel);
        if (!string.Equals(currentFingerprint, expectedDraftFingerprint, StringComparison.Ordinal))
        {
            return Snapshot(AtomicScanDraftClaimOutcome.StalePreparation, draft);
        }

        if (!string.Equals(
                draft.TargetEntityType,
                expectedTargetEntityType,
                StringComparison.OrdinalIgnoreCase))
        {
            return Snapshot(AtomicScanDraftClaimOutcome.TargetMismatch, draft);
        }

        if (!string.IsNullOrWhiteSpace(draft.SourceContentSha256))
        {
            await AcquireSourceContentLockAsync(
                draft.PortfolioId,
                draft.TargetEntityType,
                draft.SourceContentSha256,
                ct);
            var duplicate = await _db.ScanDrafts.AsNoTracking()
                .Where(candidate =>
                    candidate.PortfolioId == draft.PortfolioId
                    && candidate.Id != draft.Id
                    && candidate.Status == "Confirmed"
                    && candidate.TargetEntityType == draft.TargetEntityType
                    && candidate.SourceContentSha256 == draft.SourceContentSha256
                    && candidate.ConfirmedEntityId > 0)
                .OrderBy(candidate => candidate.ConfirmedAt ?? candidate.CreatedAt)
                .ThenBy(candidate => candidate.Id)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.TargetEntityType,
                    candidate.ConfirmedEntityId,
                })
                .FirstOrDefaultAsync(ct);
            if (duplicate is not null)
            {
                return new AtomicScanDraftClaim(
                    AtomicScanDraftClaimOutcome.DuplicateSourceContent,
                    draft.PortfolioId,
                    draft.Id,
                    draft.TargetEntityType,
                    draft.SourceStoredFileId,
                    draft.ExtractedFields,
                    draft.SourceLabel,
                    duplicate.TargetEntityType,
                    duplicate.ConfirmedEntityId);
            }
        }

        draft.Status = "Confirming";
        _auditScope.BindSemantic(
            draft,
            _db.Entry(draft),
            new AtomicSemanticAudit(
                portfolioId,
                nameof(ScanDraft),
                draftId,
                AuditLogOperation.Updated,
                UserId: confirmedByUserId,
                NewValues: "{\"Status\":\"Confirming\"}",
                ChangeReason: "Scan draft claimed for atomic confirmation."));
        return Snapshot(AtomicScanDraftClaimOutcome.Claimed, draft);
    }

    public async Task FinalizeAsync(
        AtomicScanDraftClaim claim,
        string entityType,
        int entityId,
        int confirmedByUserId,
        DateTime confirmedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        if (claim.Outcome != AtomicScanDraftClaimOutcome.Claimed || entityId <= 0)
        {
            throw new ArgumentException("Only a claimed draft and generated target id can be finalized.");
        }

        var draft = await _db.ScanDrafts.SingleAsync(
            candidate => candidate.Id == claim.DraftId
                && candidate.PortfolioId == claim.PortfolioId
                && candidate.Status == "Confirming",
            ct);
        if (claim.SourceStoredFileId is int sourceStoredFileId)
        {
            var sourceFile = await _db.StoredFiles.SingleOrDefaultAsync(
                file => file.Id == sourceStoredFileId
                    && file.PortfolioId == claim.PortfolioId
                    && file.DeletedAt == null,
                ct) ?? throw new AtomicReceiptInvariantException(
                    $"Active source file #{sourceStoredFileId} for scan draft #{claim.DraftId} was not found in its portfolio.");

            sourceFile.EntityType = entityType;
            sourceFile.EntityId = entityId;
            _auditScope.BindSemantic(
                sourceFile,
                _db.Entry(sourceFile),
                new AtomicSemanticAudit(
                    claim.PortfolioId,
                    nameof(StoredFile),
                    sourceFile.Id,
                    AuditLogOperation.Updated,
                    UserId: confirmedByUserId,
                    NewValues: $$"""{"EntityType":"{{entityType}}","EntityId":{{entityId}}}""",
                    ChangeReason: $"Source file linked to {entityType} #{entityId} by scan confirmation."));
        }

        draft.Status = "Confirmed";
        draft.ConfirmedAt = confirmedAtUtc;
        draft.ConfirmedEntityId = entityId;
        draft.ReviewedBy = confirmedByUserId.ToString();
        _auditScope.BindSemantic(
            draft,
            _db.Entry(draft),
            new AtomicSemanticAudit(
                claim.PortfolioId,
                nameof(ScanDraft),
                claim.DraftId,
                AuditLogOperation.Updated,
                UserId: confirmedByUserId,
                NewValues: $$"""{"Status":"Confirmed","TargetEntityType":"{{entityType}}","TargetEntityId":{{entityId}}}""",
                ChangeReason: $"Scan draft finalized as {entityType} #{entityId}."));
    }

    public Task<bool> IsAuthorizedForReviewAsync(
        WorkspaceReadScope scope,
        int draftId,
        DateTime utcNow,
        CancellationToken ct = default) =>
        _db.ScanDrafts.AsNoTracking()
            .WhereAuthorizedForReview(_db, scope, utcNow)
            .AnyAsync(candidate => candidate.Id == draftId, ct);

    public async Task<bool> RejectAuthorizedAsync(
        WorkspaceReadScope scope,
        int draftId,
        string? reason,
        DateTime rejectedAtUtc,
        CancellationToken ct = default)
    {
        await _locking.AcquireAsync(AtomicLockResource.ScanDraft, draftId, ct);
        var draft = await _db.ScanDrafts
            .WhereAuthorizedForReview(_db, scope, rejectedAtUtc)
            .AsTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == draftId, ct);
        if (draft is null || draft.Status is "Confirmed" or "Rejected" or "Confirming")
        {
            return false;
        }

        draft.Status = "Rejected";
        draft.ReviewedAt = rejectedAtUtc;
        draft.ReviewedBy = scope.UserId.ToString();
        var rejectionReason = Truncate(reason?.Trim(), 500);
        if (rejectionReason is not null)
        {
            draft.FailureReason = rejectionReason;
        }

        _auditScope.BindSemantic(
            draft,
            _db.Entry(draft),
            new AtomicSemanticAudit(
                scope.PortfolioId,
                nameof(ScanDraft),
                draftId,
                AuditLogOperation.Updated,
                scope.UserId,
                NewValues: "{\"Status\":\"Rejected\"}",
                ChangeReason: rejectionReason ?? "Scan draft rejected."));
        return true;
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];

    private static AtomicScanDraftClaim Snapshot(
        AtomicScanDraftClaimOutcome outcome,
        ScanDraft draft) =>
        new(outcome, draft.PortfolioId, draft.Id, draft.TargetEntityType, draft.SourceStoredFileId,
            draft.ExtractedFields, draft.SourceLabel, null, null);

    private static AtomicScanDraftClaim Empty(
        AtomicScanDraftClaimOutcome outcome,
        int portfolioId,
        int draftId) =>
        new(outcome, portfolioId, draftId, string.Empty, null, null, null, null, null);

    private async Task AcquireSourceContentLockAsync(
        int portfolioId,
        string targetEntityType,
        string sourceContentSha256,
        CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql())
        {
            return;
        }

        var lockKey = $"{portfolioId}:{targetEntityType}:{sourceContentSha256}";
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 754072))",
            ct);
    }

    private async Task ValidateSourceFileAsync(ScanDraft draft, CancellationToken ct)
    {
        if (draft.SourceStoredFileId is not int sourceStoredFileId)
        {
            return;
        }

        var sourceIsValid = await _db.StoredFiles.AnyAsync(
            file => file.Id == sourceStoredFileId
                && file.PortfolioId == draft.PortfolioId
                && file.DeletedAt == null,
            ct);
        if (!sourceIsValid)
        {
            throw new AtomicReceiptInvariantException(
                $"Active source file #{sourceStoredFileId} for scan draft #{draft.Id} was not found in its portfolio.");
        }
    }

    private async Task ValidateCaptureContextAsync(ScanDraft draft, CancellationToken ct)
    {
        var contextIsValid = await _db.ScanDrafts
            .Where(candidate => candidate.Id == draft.Id && candidate.PortfolioId == draft.PortfolioId)
            .Select(candidate =>
                (candidate.CapturePropertyId == null || _db.Properties.Any(property =>
                    property.Id == candidate.CapturePropertyId && property.PortfolioId == candidate.PortfolioId))
                && (candidate.CaptureUnitId == null || _db.Units.Any(unit =>
                    unit.Id == candidate.CaptureUnitId
                    && unit.PortfolioId == candidate.PortfolioId
                    && (candidate.CapturePropertyId == null || unit.PropertyId == candidate.CapturePropertyId)))
                && (candidate.CaptureLeaseManagementId == null || _db.LeaseManagements.Any(relationship =>
                    relationship.Id == candidate.CaptureLeaseManagementId
                    && relationship.PortfolioId == candidate.PortfolioId
                    && (candidate.CapturePropertyId == null || relationship.PropertyId == candidate.CapturePropertyId)
                    && (candidate.CaptureUnitId == null || relationship.UnitId == candidate.CaptureUnitId)))
                && (candidate.CaptureLeaseAgreementId == null || _db.LeaseAgreements.Any(agreement =>
                    agreement.Id == candidate.CaptureLeaseAgreementId
                    && agreement.PortfolioId == candidate.PortfolioId
                    && (candidate.CaptureLeaseManagementId == null
                        || agreement.LeaseManagementId == candidate.CaptureLeaseManagementId)))
                && (candidate.CaptureTenantAccountId == null || _db.TenantAccounts.Any(account =>
                    account.Id == candidate.CaptureTenantAccountId
                    && account.PortfolioId == candidate.PortfolioId
                    && (candidate.CaptureLeaseManagementId == null
                        || account.LeaseManagementId == candidate.CaptureLeaseManagementId)))
                && (candidate.CaptureTenantLedgerEntryId == null || _db.TenantLedgerEntries.Any(entry =>
                    entry.Id == candidate.CaptureTenantLedgerEntryId
                    && entry.PortfolioId == candidate.PortfolioId
                    && (candidate.CaptureTenantAccountId == null
                        || entry.TenantAccountId == candidate.CaptureTenantAccountId)))
                && (candidate.CaptureWorkOrderId == null || _db.WorkOrders.Any(workOrder =>
                    workOrder.Id == candidate.CaptureWorkOrderId
                    && workOrder.PortfolioId == candidate.PortfolioId
                    && (candidate.CapturePropertyId == null || workOrder.PropertyId == candidate.CapturePropertyId)
                    && (candidate.CaptureUnitId == null || workOrder.UnitId == candidate.CaptureUnitId)
                    && (candidate.CaptureLeaseManagementId == null
                        || workOrder.LeaseManagementId == candidate.CaptureLeaseManagementId)))
                && (candidate.CaptureApplicationId == null || _db.RentalApplications.Any(application =>
                    application.Id == candidate.CaptureApplicationId
                    && application.PortfolioId == candidate.PortfolioId
                    && (candidate.CapturePropertyId == null || application.PropertyId == candidate.CapturePropertyId)
                    && (candidate.CaptureUnitId == null || application.UnitId == candidate.CaptureUnitId)))
                && (candidate.CaptureRentalListingId == null || _db.RentalListings.Any(listing =>
                    listing.Id == candidate.CaptureRentalListingId
                    && listing.PortfolioId == candidate.PortfolioId
                    && (candidate.CapturePropertyId == null || listing.PropertyId == candidate.CapturePropertyId)
                    && (candidate.CaptureUnitId == null || listing.UnitId == candidate.CaptureUnitId))))
            .SingleAsync(ct);

        if (!contextIsValid)
        {
            throw new ScanConfirmationValidationException(
                "The record context attached to this scan is no longer valid. Reopen the record and scan again.");
        }
    }
}
