using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;

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

        var currentFingerprint = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType,
            draft.SourceStoredFileId,
            draft.ExtractedFields);
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

    private static AtomicScanDraftClaim Snapshot(
        AtomicScanDraftClaimOutcome outcome,
        ScanDraft draft) =>
        new(outcome, draft.PortfolioId, draft.Id, draft.TargetEntityType, draft.SourceStoredFileId,
            draft.ExtractedFields, null, null);

    private static AtomicScanDraftClaim Empty(
        AtomicScanDraftClaimOutcome outcome,
        int portfolioId,
        int draftId) =>
        new(outcome, portfolioId, draftId, string.Empty, null, null, null, null);

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
}
