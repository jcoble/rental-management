using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class CancelLeaseAgreementSuccessorDraftHandler
    : IAtomicCommandHandler<CancelLeaseAgreementSuccessorDraftCommand, CancelLeaseAgreementSuccessorDraftResult>,
      IAtomicReplayAuthorizer<CancelLeaseAgreementSuccessorDraftCommand>
{
    public async Task<CancelLeaseAgreementSuccessorDraftResult> HandleAsync(
        CancelLeaseAgreementSuccessorDraftCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);

        var nowUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var agreement = await LeaseAgreementDraftCommandSupport.AuthorizedRelationships(
                command, attempt.Persistence, nowUtc)
            .SelectMany(relationship => relationship.Agreements)
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == command.LeaseAgreementId
                && candidate.PortfolioId == command.PortfolioId,
                ct)
            ?? throw LeaseAgreementDraftCommandSupport.Unauthorized();

        if (agreement.DraftCanceledAtUtc != null)
        {
            return new(
                CancelLeaseAgreementSuccessorDraftOutcome.AlreadyCanceled,
                command.LeaseManagementId,
                agreement.Id,
                agreement.DraftCanceledAtUtc,
                agreement.DraftCanceledByUserId,
                agreement.DraftCancellationReason,
                "The successor Agreement draft has already been canceled.");
        }

        if (agreement.ChangeType is not (LeaseAgreementChangeType.Correction
                or LeaseAgreementChangeType.Restatement
                or LeaseAgreementChangeType.Renewal
                or LeaseAgreementChangeType.MonthToMonth)
            || (agreement.ReplacesAgreementId == null && agreement.RenewsAgreementId == null))
        {
            return Error(
                CancelLeaseAgreementSuccessorDraftOutcome.NotSuccessorDraft,
                command,
                "Only a correction, restatement, renewal, or month-to-month successor draft can be canceled.");
        }

        if (agreement.IssuedAtUtc != null || agreement.IssuedArtifactId != null
            || agreement.FullyExecutedAtUtc != null || agreement.ExecutedArtifactId != null
            || agreement.VoidedAtUtc != null)
        {
            return Error(
                CancelLeaseAgreementSuccessorDraftOutcome.IssuedOrExecuted,
                command,
                "An issued, executed, or void Agreement cannot be canceled as an abandoned draft.");
        }

        agreement.DraftCanceledAtUtc = nowUtc;
        agreement.DraftCanceledByUserId = command.ActorUserId;
        agreement.DraftCancellationReason = command.CancellationReason.Trim();
        agreement.UpdatedAtUtc = nowUtc;

        attempt.BindSemanticAudit(agreement, LeaseAgreementDraftCommandSupport.Updated(
            command, agreement.Id, "Canceled an abandoned successor Agreement draft."));
        LeaseAgreementDraftCommandSupport.StageOutbox(
            attempt, command, nowUtc, agreement.Id, "agreement-successor-draft-canceled");

        return new(
            CancelLeaseAgreementSuccessorDraftOutcome.Canceled,
            command.LeaseManagementId,
            agreement.Id,
            nowUtc,
            command.ActorUserId,
            agreement.DraftCancellationReason,
            null);
    }

    public Task AuthorizeReplayAsync(
        CancelLeaseAgreementSuccessorDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        return LeaseAgreementDraftCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
    }

    private static void Validate(CancelLeaseAgreementSuccessorDraftCommand command)
    {
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);
        if (command.LeaseAgreementId <= 0
            || string.IsNullOrWhiteSpace(command.CancellationReason)
            || command.CancellationReason.Trim().Length > 1000)
        {
            throw new ArgumentException(
                "A successor Agreement id and cancellation reason of at most 1000 characters are required.");
        }
    }

    private static CancelLeaseAgreementSuccessorDraftResult Error(
        CancelLeaseAgreementSuccessorDraftOutcome outcome,
        CancelLeaseAgreementSuccessorDraftCommand command,
        string error) => new(
        outcome,
        command.LeaseManagementId,
        command.LeaseAgreementId,
        null,
        null,
        null,
        error);
}
