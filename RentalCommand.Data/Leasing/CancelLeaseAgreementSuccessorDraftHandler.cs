using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class CancelLeaseAgreementSuccessorDraftHandler
    : IAtomicCommandHandler<CancelLeaseAgreementSuccessorDraftCommand, CancelLeaseAgreementSuccessorDraftResult>
{
    private readonly RentalCommandDbContext _db;

    public CancelLeaseAgreementSuccessorDraftHandler(RentalCommandDbContext db) => _db = db;

    public async Task<CancelLeaseAgreementSuccessorDraftResult> HandleAsync(
        CancelLeaseAgreementSuccessorDraftCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw LeasingWriteSupport.RetiredPath();

    public async Task<CancelLeaseAgreementSuccessorDraftResult> ExecuteAsync(
        CancelLeaseAgreementSuccessorDraftCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var nowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var agreement = await LeaseAgreementDraftCommandSupport.AuthorizedRelationships(
                command, _db, nowUtc)
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

        var isOrdinarySuccessor = agreement.ChangeType is (LeaseAgreementChangeType.Correction
                or LeaseAgreementChangeType.Restatement
                or LeaseAgreementChangeType.Renewal
                or LeaseAgreementChangeType.MonthToMonth)
            && (agreement.ReplacesAgreementId != null || agreement.RenewsAgreementId != null);
        if (!isOrdinarySuccessor && agreement.ReissuesAgreementId == null)
        {
            return Error(
                CancelLeaseAgreementSuccessorDraftOutcome.NotSuccessorDraft,
                command,
                "Only a successor or reissue Agreement draft can be canceled.");
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

        context.BindSemanticAudit(agreement, LeaseAgreementDraftCommandSupport.Updated(
            command, agreement.Id, "Canceled an abandoned successor Agreement draft."));
        LeaseAgreementDraftCommandSupport.StageOutbox(
            context, command, nowUtc, agreement.Id, "agreement-successor-draft-canceled");

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
        CancelLeaseAgreementSuccessorDraftCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        return LeaseAgreementDraftCommandSupport.AuthorizeReplayAsync(command, _db, ct);
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
