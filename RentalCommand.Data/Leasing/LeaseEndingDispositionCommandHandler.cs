using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public static class LeasingWriteSupport
{
    public static TransactionalWrite<TCommand, TResult> Write<TCommand, TResult>(
        RentalCommandDbContext db,
        TCommand command)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        object write = command switch
        {
            PrepareMoveInCommand value => Build(
                "lease-management.prepare-move-in", "lease-management.prepare-move-in.v2",
                new WriteLockPlan(WriteLockProtocol.PrepareMoveIn, value.UnitId),
                value, new PrepareMoveInHandler(db).ExecuteAsync, new PrepareMoveInHandler(db).AuthorizeReplayAsync),
            RecordLeaseEndingDispositionCommand value => Build(
                "lease-management.ending-disposition", "lease-management.ending-disposition.v1",
                WriteLockPlan.None, value, new RecordLeaseEndingDispositionHandler(db).ExecuteAsync,
                new RecordLeaseEndingDispositionHandler(db).AuthorizeReplayAsync),
            CancelPlannedRelationshipCommand value => Build(
                "lease-management.cancel-planned", "lease-management.cancel-planned.v1",
                LeaseManagement(value.LeaseManagementId), value,
                new CancelPlannedRelationshipHandler(db).ExecuteAsync,
                new CancelPlannedRelationshipHandler(db).AuthorizeReplayAsync),
            TransferLeaseManagementCommand value => Build(
                "lease-management.transfer-unit", "lease-management.transfer-unit.v1",
                Transfer(value), value, new TransferLeaseManagementHandler(db).ExecuteAsync,
                new TransferLeaseManagementHandler(db).AuthorizeReplayAsync),
            CancelLeaseAgreementSuccessorDraftCommand value => Build(
                "lease-agreement.successor-draft.cancel", "lease-agreement.successor-draft.cancel.v1",
                Agreement(value), value, new CancelLeaseAgreementSuccessorDraftHandler(db).ExecuteAsync,
                new CancelLeaseAgreementSuccessorDraftHandler(db).AuthorizeReplayAsync),
            CreateLeaseAddendumDraftCommand value => Build(
                "lease-addendum.draft.create", "lease-addendum.draft.mutation.v1",
                LeaseManagement(value.LeaseManagementId), value,
                new CreateLeaseAddendumDraftHandler(db).ExecuteAsync,
                new CreateLeaseAddendumDraftHandler(db).AuthorizeReplayAsync),
            EditLeaseAddendumDraftCommand value => Build(
                "lease-addendum.draft.edit", "lease-addendum.draft.mutation.v1",
                LeaseManagement(value.LeaseManagementId), value,
                new EditLeaseAddendumDraftHandler(db).ExecuteAsync,
                new EditLeaseAddendumDraftHandler(db).AuthorizeReplayAsync),
            CorrectLeaseAddendumDraftCommand value => Build(
                "lease-addendum.draft.correct", "lease-addendum.draft.mutation.v1",
                LeaseManagement(value.LeaseManagementId), value,
                new CorrectLeaseAddendumDraftHandler(db).ExecuteAsync,
                new CorrectLeaseAddendumDraftHandler(db).AuthorizeReplayAsync),
            EditLeaseAgreementDraftCommand value => Build(
                "lease-agreement.draft.edit", "lease-agreement.draft.edit.v1",
                Agreement(value), value, new EditLeaseAgreementDraftHandler(db).ExecuteAsync,
                new EditLeaseAgreementDraftHandler(db).AuthorizeReplayAsync),
            CreateLeaseAgreementSuccessorDraftCommand value => Build(
                "lease-agreement.successor-draft.create", "lease-agreement.successor-draft.create.v2",
                Agreement(value), value, new CreateLeaseAgreementSuccessorDraftHandler(db).ExecuteAsync,
                new CreateLeaseAgreementSuccessorDraftHandler(db).AuthorizeReplayAsync),
            ReplaceIssuedAgreementWithDraftCommand value => Build(
                "lease-agreement.issued-replacement-draft.create", "lease-agreement.successor-draft.create.v2",
                Agreement(value), value, new ReplaceIssuedAgreementWithDraftHandler(db).ExecuteAsync,
                new ReplaceIssuedAgreementWithDraftHandler(db).AuthorizeReplayAsync),
            CreatePropertyDispositionCommand value => Build(
                "property-disposition.create", "property-disposition.create.v1",
                new WriteLockPlan(WriteLockProtocol.PropertyDisposition,
                    value.PropertyId),
                value, new CreatePropertyDispositionHandler(db).ExecuteAsync,
                new CreatePropertyDispositionHandler(db).AuthorizeReplayAsync),
            GivePossessionCommand value => Build(
                "lease-management.give-possession", "lease-management.give-possession.v1",
                Possession(value.UnitId, value.LeaseManagementId), value,
                new GivePossessionHandler(db).ExecuteAsync, new GivePossessionHandler(db).AuthorizeReplayAsync),
            ReconcileHistoricalPossessionCommand value => Build(
                "lease-management.reconcile-historical-possession",
                "lease-management.reconcile-historical-possession.v1",
                Possession(value.UnitId, value.LeaseManagementId), value,
                new ReconcileHistoricalPossessionHandler(db).ExecuteAsync,
                new ReconcileHistoricalPossessionHandler(db).AuthorizeReplayAsync),
            ConfirmMoveInCommand value => Build(
                "lease-management.confirm-move-in", "lease-management.confirm-move-in.v1",
                WriteLockPlan.None, value, new ConfirmMoveInHandler(db).ExecuteAsync,
                new ConfirmMoveInHandler(db).AuthorizeReplayAsync),
            ReturnPossessionCommand value => Build(
                "lease-management.return-possession", "lease-management.return-possession.v1",
                Possession(value.UnitId, value.LeaseManagementId), value,
                new ReturnPossessionHandler(db).ExecuteAsync,
                new ReturnPossessionHandler(db).AuthorizeReplayAsync),
            CompleteTurnoverCommand value => Build(
                "unit.complete-turnover", "unit.complete-turnover.v1",
                WriteLockPlan.None, value, new CompleteTurnoverHandler(db).ExecuteAsync,
                new CompleteTurnoverHandler(db).AuthorizeReplayAsync),
            VoidLeaseAgreementCommand value => Build(
                "lease-agreement.void", "lease-agreement.void.v1",
                LeaseManagement(value.LeaseManagementId), value,
                new VoidLeaseAgreementHandler(db).ExecuteAsync,
                new VoidLeaseAgreementHandler(db).AuthorizeReplayAsync),
            VoidLeaseAddendumCommand value => Build(
                "lease-addendum.void", "lease-addendum.void.v1",
                LeaseManagement(value.LeaseManagementId), value,
                new VoidLeaseAddendumHandler(db).ExecuteAsync,
                new VoidLeaseAddendumHandler(db).AuthorizeReplayAsync),
            CloseTenantAccountCommand value => Build(
                "tenant-account.close", "tenant-account.close.v1",
                WriteLockPlan.None, value, new CloseTenantAccountHandler(db).ExecuteAsync,
                new CloseTenantAccountHandler(db).AuthorizeReplayAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return (TransactionalWrite<TCommand, TResult>)write;
    }

    private static WriteLockPlan LeaseManagement(int id) => new(
        WriteLockProtocol.LeaseManagement, id);

    private static WriteLockPlan Possession(int unitId, int leaseManagementId) => new(
        WriteLockProtocol.Possession,
        unitId,
        leaseManagementId);

    private static WriteLockPlan Agreement(ILeaseAgreementDraftCommand command) => new(
        WriteLockProtocol.LeaseAgreementDraft,
        command.AuthSessionId,
        command.AccessContextId,
        command.LeaseManagementId);

    private static WriteLockPlan Transfer(TransferLeaseManagementCommand command)
    {
        var unitIds = new[] { command.SourceUnitId, command.DestinationUnitId }.OrderBy(id => id).ToArray();
        return new(WriteLockProtocol.LeaseTransfer,
            unitIds[0], unitIds[1],
            command.SourceLeaseManagementId);
    }

    private static TransactionalWrite<TCommand, TResult> Build<TCommand, TResult>(
        string operationName,
        string resultContract,
        WriteLockPlan lockPlan,
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeAsync)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull => new(operationName, WriteIdempotencyPolicy.Required,
            command, resultContract, lockPlan, executeAsync, authorizeAsync);

    internal static InvalidOperationException RetiredPath() => new(
        "Legacy atomic leasing writes are retired; use the shared write executor.");
}

public sealed class RecordLeaseEndingDispositionHandler
{
    private readonly RentalCommandDbContext _db;

    public RecordLeaseEndingDispositionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RecordLeaseEndingDispositionResult> ExecuteAsync(
        RecordLeaseEndingDispositionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync(
            "LeaseManagement", command.LeaseManagementId, ct);

        var nowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var relationship = await AuthorizedRelationship(_db, command, nowUtc)
            .SingleOrDefaultAsync(ct);
        if (relationship is null)
        {
            throw new UnauthorizedAccessException(
                "The lease relationship is not authorized in the current property scope.");
        }
        if (relationship.CanceledAtUtc is not null
            || relationship.PossessionGivenAtUtc is null
            || relationship.PossessionReturnedAtUtc is not null
            || relationship.AccountClosedAtUtc is not null)
        {
            return Empty(
                RecordLeaseEndingDispositionOutcome.RelationshipNotEligible,
                command,
                "Only an occupied, open lease relationship can receive an ending disposition.");
        }

        var isMoveOut = command.Disposition == LeaseManagementEndingDisposition.NonRenewalMoveOut;
        if (isMoveOut
            && (command.NoticeGivenAtUtc is null
                || command.PlannedMoveOutAtUtc is null
                || command.PlannedMoveOutAtUtc < command.NoticeGivenAtUtc))
        {
            return Empty(
                RecordLeaseEndingDispositionOutcome.InvalidDates,
                command,
                "Move-out requires a notice date and an effective move-out date on or after notice.");
        }

        relationship.EndingDisposition = command.Disposition;
        relationship.EndingDispositionDecidedAtUtc =
            command.Disposition == LeaseManagementEndingDisposition.Undecided ? null : nowUtc;
        relationship.EndingDispositionDecidedByUserId =
            command.Disposition == LeaseManagementEndingDisposition.Undecided
                ? null
                : command.ActorUserId;
        relationship.NoticeGivenAtUtc = isMoveOut ? command.NoticeGivenAtUtc : null;
        relationship.PlannedMoveOutAtUtc = isMoveOut ? command.PlannedMoveOutAtUtc : null;
        relationship.UpdatedAtUtc = nowUtc;
        relationship.RowVersion = Guid.NewGuid();

        context.BindSemanticAudit(
            relationship,
            new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(LeaseManagement),
                relationship.Id,
                AuditLogOperation.Updated,
                UserId: command.ActorUserId,
                ChangeReason: command.DecisionReason.Trim()));
        context.StageOutbox(PossessionOutbox.Create(
            command.PortfolioId,
            command.DeliveryIdempotencyKey,
            nowUtc,
            "lease-ending-disposition-recorded",
            nameof(LeaseManagement),
            relationship.Id,
            relationship.Id,
            relationship.UnitId));
        await context.FlushBusinessAsync(ct);

        return new(
            RecordLeaseEndingDispositionOutcome.Recorded,
            relationship.Id,
            relationship.EndingDisposition,
            relationship.EndingDispositionDecidedAtUtc,
            relationship.EndingDispositionDecidedByUserId,
            relationship.NoticeGivenAtUtc,
            relationship.PlannedMoveOutAtUtc,
            null);
    }

    public async Task AuthorizeReplayAsync(
        RecordLeaseEndingDispositionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var nowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await AuthorizedRelationship(_db, command, nowUtc).AnyAsync(ct))
        {
            throw new UnauthorizedAccessException(
                "The lease relationship is not authorized in the current property scope.");
        }
    }

    private static IQueryable<LeaseManagement> AuthorizedRelationship(
        RentalCommandDbContext db,
        RecordLeaseEndingDispositionCommand command,
        DateTime nowUtc) =>
        LeaseAgreementDraftCommandSupport.AuthorizedRelationships(command, db, nowUtc)
            .Where(relationship => relationship.UnitId == command.UnitId);

    private static void Validate(RecordLeaseEndingDispositionCommand command)
    {
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);
        if (command.UnitId <= 0
            || !Enum.IsDefined(command.Disposition)
            || string.IsNullOrWhiteSpace(command.DecisionReason)
            || command.DecisionReason.Trim().Length > 1000
            || command.NoticeGivenAtUtc is { Kind: not DateTimeKind.Utc }
            || command.PlannedMoveOutAtUtc is { Kind: not DateTimeKind.Utc }
            || (command.Disposition != LeaseManagementEndingDisposition.NonRenewalMoveOut
                && (command.NoticeGivenAtUtc is not null || command.PlannedMoveOutAtUtc is not null)))
        {
            throw new ArgumentException(
                "The ending disposition, reason, or effective dates are invalid.");
        }
    }

    private static RecordLeaseEndingDispositionResult Empty(
        RecordLeaseEndingDispositionOutcome outcome,
        RecordLeaseEndingDispositionCommand command,
        string error) =>
        new(
            outcome,
            command.LeaseManagementId,
            command.Disposition,
            null,
            null,
            command.NoticeGivenAtUtc,
            command.PlannedMoveOutAtUtc,
            error);
}
