using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;
using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data.Scanning;

/// <summary>
/// Atomic scan lifecycle owner. Target writers are supplied as a concrete sealed transaction-safe
/// dependency so claiming, canonical writes, file linkage, audit, and receipt commit together.
/// </summary>
public sealed class ConfirmScanDraftHandler
    : IAtomicCommandHandler<ConfirmScanDraftCommand, ConfirmScanDraftResult>
{
    private readonly IScanConfirmationTargetWriter _targetWriter;
    private readonly RentalCommandDbContext _db;

    public ConfirmScanDraftHandler(IScanConfirmationTargetWriter targetWriter, RentalCommandDbContext db)
    {
        _targetWriter = targetWriter;
        _db = db;
    }

    public async Task<ConfirmScanDraftResult> HandleAsync(
        ConfirmScanDraftCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        command.Target.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedDraftFingerprint);
        if (!_targetWriter.Supports(command.Target.Kind))
        {
            throw new ScanConfirmationValidationException(
                $"Confirmation for {command.Target.Kind} is not available yet.");
        }

        // Normal executions need the same current-session/access-revision/resource proof as
        // receipt replays. Revalidate before claiming so authorization and every protected write
        // share this transaction.
        await _targetWriter.AuthorizeReplayAsync(command, context, ct);

        var claim = await AtomicScanConfirmationPersistence.TryClaimAsync(_db,
            context, command.PortfolioId,
            command.DraftId,
            command.Target.EntityType,
            command.ExpectedDraftFingerprint,
            command.ConfirmedByUserId,
            ct);
        switch (claim.Outcome)
        {
            case AtomicScanDraftClaimOutcome.NotFound:
                return Result(ConfirmScanDraftOutcome.DraftNotFound, command, error: "Draft not found.");
            case AtomicScanDraftClaimOutcome.NotReady:
                throw new ScanConfirmationValidationException("Draft is not ready to confirm.");
            case AtomicScanDraftClaimOutcome.Rejected:
                return Result(ConfirmScanDraftOutcome.DraftRejected, command, error: "Draft is rejected.");
            case AtomicScanDraftClaimOutcome.TargetMismatch:
                throw new ScanConfirmationValidationException(
                    "Draft target does not match the reviewed confirmation target.");
            case AtomicScanDraftClaimOutcome.StalePreparation:
                throw new ScanConfirmationValidationException(
                    "Draft changed after it was reviewed. Review the latest extraction and confirm again.");
            case AtomicScanDraftClaimOutcome.AlreadyConfirmed:
                var existingReceipt = command.Target.Kind == ScanConfirmationTargetKind.Payment
                    ? await (
                        from entry in _db.Set<TenantLedgerEntry>()
                        join account in _db.Set<TenantAccount>()
                            on new { entry.TenantAccountId, entry.PortfolioId }
                            equals new { TenantAccountId = account.Id, account.PortfolioId }
                        where entry.PortfolioId == command.PortfolioId
                            && entry.TenantAccountId == claim.CanonicalEntityId
                            && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                            && entry.BusinessKey == $"scan-receipt:{command.DraftId}"
                        select new
                        {
                            LedgerEntryId = (long?)entry.Id,
                            UnitId = (int?)account.LeaseManagement!.UnitId,
                        }).SingleOrDefaultAsync(ct)
                    : null;
                var existingAgreement = command.Target.Kind == ScanConfirmationTargetKind.LeaseAgreement
                    ? await (
                        from agreement in _db.Set<LeaseAgreement>()
                        where agreement.PortfolioId == command.PortfolioId
                            && agreement.Id == claim.CanonicalEntityId
                        select new
                        {
                            LeaseManagementId = (int?)agreement.LeaseManagementId,
                            UnitId = (int?)agreement.LeaseManagement!.UnitId,
                        }).SingleOrDefaultAsync(ct)
                    : null;
                var existingLoanPaymentId = command.Target.Kind == ScanConfirmationTargetKind.Loan
                    && command.Target.Loan?.ExistingLoanPaymentId is int requestedPaymentId
                    ? await _db.Set<LoanPayment>()
                        .Where(payment =>
                            payment.Id == requestedPaymentId
                            && payment.LoanId == claim.CanonicalEntityId
                            && payment.PortfolioId == command.PortfolioId)
                        .Select(payment => (int?)payment.Id)
                        .SingleOrDefaultAsync(ct)
                    : null;
                var existingEndingRelationship = command.Target.Kind == ScanConfirmationTargetKind.LeaseEndingNotice
                    ? await _db.Set<LeaseManagement>()
                        .Where(relationship =>
                            relationship.Id == claim.CanonicalEntityId
                            && relationship.PortfolioId == command.PortfolioId)
                        .Select(relationship => new
                        {
                            LeaseManagementId = (int?)relationship.Id,
                            UnitId = (int?)relationship.UnitId,
                        })
                        .SingleOrDefaultAsync(ct)
                    : null;
                return new ConfirmScanDraftResult(
                    ConfirmScanDraftOutcome.AlreadyConfirmed,
                    command.DraftId,
                    command.Target.EntityType,
                    claim.CanonicalEntityId,
                    existingReceipt?.UnitId ?? existingAgreement?.UnitId ?? existingEndingRelationship?.UnitId,
                    LedgerEntryId: existingReceipt?.LedgerEntryId,
                    LeaseManagementId: existingAgreement?.LeaseManagementId ?? existingEndingRelationship?.LeaseManagementId,
                    LoanPaymentId: existingLoanPaymentId);
            case AtomicScanDraftClaimOutcome.DuplicateSourceContent:
                return new ConfirmScanDraftResult(
                    ConfirmScanDraftOutcome.DuplicateSourceContent,
                    command.DraftId,
                    command.Target.EntityType,
                    claim.CanonicalEntityId,
                    Error: "This scan source has already been confirmed for this target. Open the existing record instead of confirming it again.");
            case AtomicScanDraftClaimOutcome.Claimed:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(claim.Outcome));
        }

        await context.FlushBusinessAsync(ct);
        var target = await _targetWriter.WriteAsync(command, claim.ExtractedFieldsJson, context, ct);
        if (target.EntityId <= 0)
        {
            throw new InvalidOperationException("A scan target writer must return a generated positive entity id.");
        }

        var canonicalEntityType = target.CanonicalEntityType ?? command.Target.EntityType;
        await AtomicScanConfirmationPersistence.FinalizeAsync(_db,
            context, claim,
            canonicalEntityType,
            target.EntityId,
            command.ConfirmedByUserId,
            command.ConfirmedAtUtc,
            ct);
        await context.FlushBusinessAsync(ct);
        if (!target.TargetAuditRecorded)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                canonicalEntityType,
                target.EntityId,
                AuditLogOperation.Created,
                UserId: command.ConfirmedByUserId,
                OldValues: claim.ExtractedFieldsJson,
                ChangeReason: $"Created from scan draft #{command.DraftId}."));
        }

        return new ConfirmScanDraftResult(
            ConfirmScanDraftOutcome.Confirmed,
            command.DraftId,
            command.Target.EntityType,
            target.EntityId,
            target.UnitId,
            LedgerEntryId: target.LedgerEntryId,
            LeaseManagementId: target.LeaseManagementId,
            LoanPaymentId: target.LoanPaymentId);
    }

    private static ConfirmScanDraftResult Result(
        ConfirmScanDraftOutcome outcome,
        ConfirmScanDraftCommand command,
        string? error = null) =>
        new(outcome, command.DraftId, command.Target.EntityType, null, Error: error);

    public Task AuthorizeReplayAsync(
        ConfirmScanDraftCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        _targetWriter.AuthorizeReplayAsync(command, context, ct);
}
