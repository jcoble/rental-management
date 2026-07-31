using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class RecordLeaseEndingDispositionHandler
    : IAtomicCommandHandler<RecordLeaseEndingDispositionCommand, RecordLeaseEndingDispositionResult>
{
    private readonly RentalCommandDbContext _db;

    public RecordLeaseEndingDispositionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RecordLeaseEndingDispositionResult> HandleAsync(
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
