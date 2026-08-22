using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class CancelPlannedRelationshipRule
{
    private readonly RentalCommandDbContext _db;

    public CancelPlannedRelationshipRule(RentalCommandDbContext db) => _db = db;

    public async Task<CancelPlannedRelationshipResult> ExecuteAsync(
        CancelPlannedRelationshipCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        if (!await AuthorizedRelationships(
                command, _db, businessNowUtc, securityNowUtc).AnyAsync(ct))
        {
            throw Unauthorized();
        }

        var mutation = await AtomicLeaseMutationPersistence.CancelPlannedRelationshipAsync(_db,
            context, command.PortfolioId,
            command.LeaseManagementId,
            command.Accesses.Select(item => new AtomicCancelPlannedAccessInput(
                item.TenantUserAccessId, item.Disposition)).ToArray(),
            command.CreatedByUserId,
            businessNowUtc,
            command.CancellationReasonCode,
            command.CancellationNote,
            command.DraftCancellationReason,
            ct);

        if (mutation.Outcome != CancelPlannedRelationshipOutcome.Canceled)
        {
            return Error(mutation.Outcome, command, mutation.CanceledAtUtc);
        }

        foreach (var agreementId in mutation.CanceledAgreementDraftIds)
        {
            context.StageSemanticEvent(Updated(command, nameof(LeaseAgreement), agreementId,
                "Unissued Agreement draft canceled with the planned relationship."), businessNowUtc);
        }
        foreach (var addendumId in mutation.CanceledAddendumDraftIds)
        {
            context.StageSemanticEvent(Updated(command, nameof(LeaseAddendum), addendumId,
                "Unissued Addendum draft canceled with the planned relationship."), businessNowUtc);
        }
        foreach (var accessId in mutation.RevokedAccessIds)
        {
            context.StageSemanticEvent(Updated(command, nameof(TenantUserAccess), accessId,
                "Tenant access revoked under the explicit cancellation policy."), businessNowUtc);
        }
        if (mutation.TenantAccountId is int tenantAccountId)
        {
            context.StageSemanticEvent(Updated(command, nameof(TenantAccount), tenantAccountId,
                "Empty Tenant Account closed with the canceled planned relationship."), businessNowUtc);
        }
        context.StageSemanticEvent(Updated(command, nameof(LeaseManagement),
            command.LeaseManagementId, "Planned lease relationship canceled before posdb."), businessNowUtc);
        context.StageOutbox(PossessionOutbox.Create(
            command.PortfolioId,
            command.DeliveryIdempotencyKey,
            businessNowUtc,
            "planned-relationship-canceled",
            nameof(LeaseManagement),
            command.LeaseManagementId,
            command.LeaseManagementId,
            command.UnitId));

        return new(
            CancelPlannedRelationshipOutcome.Canceled,
            command.LeaseManagementId,
            command.UnitId,
            mutation.CanceledAtUtc,
            mutation.CanceledAtUtc,
            mutation.CanceledAgreementDraftIds,
            mutation.CanceledAddendumDraftIds,
            mutation.RevokedAccessIds,
            mutation.RetainedAccessIds,
            null);
    }

    public async Task AuthorizeReplayAsync(
        CancelPlannedRelationshipCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await AuthorizedRelationships(
                command, _db, command.BusinessNowUtc, securityNowUtc).AnyAsync(ct))
        {
            throw Unauthorized();
        }
    }

    private static IQueryable<LeaseManagement> AuthorizedRelationships(
        CancelPlannedRelationshipCommand command,
        RentalCommandDbContext db,
        DateTime businessNowUtc,
        DateTime securityNowUtc) =>
        PossessionCommandAuthorization.AuthorizedRelationships(
            db,
            command.PortfolioId,
            command.LeaseManagementId,
            command.UnitId,
            command.CreatedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            businessNowUtc,
            securityNowUtc);

    private static void Validate(CancelPlannedRelationshipCommand command)
    {
        PossessionCommandAuthorization.ValidateShape(
            command.PortfolioId,
            command.LeaseManagementId,
            command.UnitId,
            command.CreatedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            command.BusinessNowUtc,
            command.DeliveryIdempotencyKey);
        if (string.IsNullOrWhiteSpace(command.CancellationReasonCode)
            || command.CancellationReasonCode.Trim().Length > 40
            || command.CancellationNote?.Trim().Length > 2000
            || string.IsNullOrWhiteSpace(command.DraftCancellationReason)
            || command.DraftCancellationReason.Trim().Length > 1000
            || command.Accesses.Select(item => item.TenantUserAccessId).Distinct().Count()
                != command.Accesses.Count
            || command.Accesses.Any(item => item.TenantUserAccessId <= 0
                || !Enum.IsDefined(item.Disposition)))
        {
            throw new ArgumentException("Cancellation reasons and access dispositions are invalid.");
        }
    }

    private static CancelPlannedRelationshipResult Error(
        CancelPlannedRelationshipOutcome outcome,
        CancelPlannedRelationshipCommand command,
        DateTime? canceledAtUtc) => new(
        outcome,
        command.LeaseManagementId,
        command.UnitId,
        canceledAtUtc,
        outcome == CancelPlannedRelationshipOutcome.AlreadyCanceled ? canceledAtUtc : null,
        [],
        [],
        [],
        [],
        outcome switch
        {
            CancelPlannedRelationshipOutcome.AlreadyCanceled =>
                "The planned lease relationship has already been canceled.",
            CancelPlannedRelationshipOutcome.PossessionAlreadyGiven =>
                "A relationship cannot be canceled after possession has been given; use the possession-return workflow.",
            CancelPlannedRelationshipOutcome.IssuedArtifactsRequireResolution =>
                "An issued Agreement or Addendum must be resolved by its explicit void or replacement command before cancellation.",
            CancelPlannedRelationshipOutcome.FinancialResolutionRequired =>
                "Posted money, an active payment workflow, or active autopay must be resolved with append-only refund/reversal commands before cancellation.",
            CancelPlannedRelationshipOutcome.InvalidAccessPolicy =>
                "Every active tenant access must have exactly one explicit revoke or retain disposition.",
            _ => "The planned lease relationship could not be canceled.",
        });

    private static AtomicSemanticAudit Updated(
        CancelPlannedRelationshipCommand command,
        string entityType,
        int entityId,
        string reason) => new(
        command.PortfolioId,
        entityType,
        entityId,
        AuditLogOperation.Updated,
        UserId: command.CreatedByUserId,
        ChangeReason: reason);

    private static UnauthorizedAccessException Unauthorized() => new(
        "The lease relationship is not authorized in the current property scope.");
}
