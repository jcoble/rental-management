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
public sealed class ConfirmScanDraftHandler<TTargetWriter>
    : IAtomicCommandHandler<ConfirmScanDraftCommand, ConfirmScanDraftResult>,
      IAtomicReplayAuthorizer<ConfirmScanDraftCommand>
    where TTargetWriter : class, IScanConfirmationTargetWriter
{
    private readonly TTargetWriter _targetWriter;

    public ConfirmScanDraftHandler(TTargetWriter targetWriter) => _targetWriter = targetWriter;

    public async Task<ConfirmScanDraftResult> HandleAsync(
        ConfirmScanDraftCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        command.Target.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedDraftFingerprint);
        if (!_targetWriter.Supports(command.Target.Kind))
        {
            throw new ScanConfirmationValidationException(
                $"Confirmation for {command.Target.Kind} is not available yet.");
        }

        var claim = await attempt.ScanConfirmation.TryClaimAsync(
            command.PortfolioId,
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
                        from entry in attempt.Persistence.Query<TenantLedgerEntry>()
                        join account in attempt.Persistence.Query<TenantAccount>()
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
                return new ConfirmScanDraftResult(
                    ConfirmScanDraftOutcome.AlreadyConfirmed,
                    command.DraftId,
                    command.Target.EntityType,
                    claim.CanonicalEntityId,
                    existingReceipt?.UnitId,
                    LedgerEntryId: existingReceipt?.LedgerEntryId);
            case AtomicScanDraftClaimOutcome.Claimed:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(claim.Outcome));
        }

        await attempt.FlushBusinessAsync(ct);
        var target = await _targetWriter.WriteAsync(command, claim.ExtractedFieldsJson, attempt, ct);
        if (target.EntityId <= 0)
        {
            throw new InvalidOperationException("A scan target writer must return a generated positive entity id.");
        }

        var canonicalEntityType = target.CanonicalEntityType ?? command.Target.EntityType;
        await attempt.ScanConfirmation.FinalizeAsync(
            claim,
            canonicalEntityType,
            target.EntityId,
            command.ConfirmedByUserId,
            command.ConfirmedAtUtc,
            ct);
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            canonicalEntityType,
            target.EntityId,
            AuditLogOperation.Created,
            UserId: command.ConfirmedByUserId,
            OldValues: claim.ExtractedFieldsJson,
            ChangeReason: $"Created from scan draft #{command.DraftId}."));

        return new ConfirmScanDraftResult(
            ConfirmScanDraftOutcome.Confirmed,
            command.DraftId,
            command.Target.EntityType,
            target.EntityId,
            target.UnitId,
            LedgerEntryId: target.LedgerEntryId);
    }

    private static ConfirmScanDraftResult Result(
        ConfirmScanDraftOutcome outcome,
        ConfirmScanDraftCommand command,
        string? error = null) =>
        new(outcome, command.DraftId, command.Target.EntityType, null, Error: error);

    public Task AuthorizeReplayAsync(
        ConfirmScanDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) => _targetWriter.AuthorizeReplayAsync(command, persistence, ct);
}
