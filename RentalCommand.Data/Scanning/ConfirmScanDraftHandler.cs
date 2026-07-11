using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Data.Scanning;

/// <summary>
/// Atomic scan lifecycle owner. Target writers are supplied as a concrete sealed transaction-safe
/// dependency, keeping production activation separate from this inert foundation.
/// </summary>
public sealed class ConfirmScanDraftHandler<TTargetWriter>
    : IAtomicCommandHandler<ConfirmScanDraftCommand, ConfirmScanDraftResult>
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
        if (!_targetWriter.Supports(command.Target.Kind))
        {
            return Result(ConfirmScanDraftOutcome.UnsupportedTarget, command, error: "Target writer is not installed.");
        }

        var claim = await attempt.ScanConfirmation.TryClaimAsync(
            command.PortfolioId,
            command.DraftId,
            command.Target.EntityType,
            command.ConfirmedByUserId,
            ct);
        switch (claim.Outcome)
        {
            case AtomicScanDraftClaimOutcome.NotFound:
                return Result(ConfirmScanDraftOutcome.DraftNotFound, command, error: "Draft not found.");
            case AtomicScanDraftClaimOutcome.NotReady:
                return Result(ConfirmScanDraftOutcome.DraftNotReady, command, error: "Draft is not ready to confirm.");
            case AtomicScanDraftClaimOutcome.Rejected:
                return Result(ConfirmScanDraftOutcome.DraftRejected, command, error: "Draft is rejected.");
            case AtomicScanDraftClaimOutcome.TargetMismatch:
                return Result(ConfirmScanDraftOutcome.TargetMismatch, command, error: "Draft target does not match command target.");
            case AtomicScanDraftClaimOutcome.AlreadyConfirmed:
                return new ConfirmScanDraftResult(
                    ConfirmScanDraftOutcome.AlreadyConfirmed,
                    command.DraftId,
                    claim.CanonicalEntityType ?? claim.TargetEntityType,
                    claim.CanonicalEntityId);
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

        await attempt.ScanConfirmation.FinalizeAsync(
            claim,
            command.Target.EntityType,
            target.EntityId,
            command.ConfirmedByUserId,
            command.ConfirmedAtUtc,
            ct);
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            command.Target.EntityType,
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
            target.UnitId);
    }

    private static ConfirmScanDraftResult Result(
        ConfirmScanDraftOutcome outcome,
        ConfirmScanDraftCommand command,
        string? error = null) =>
        new(outcome, command.DraftId, command.Target.EntityType, null, Error: error);
}
