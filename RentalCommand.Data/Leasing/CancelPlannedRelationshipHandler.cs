using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class CancelPlannedRelationshipHandler
    : IAtomicCommandHandler<CancelPlannedRelationshipCommand, CancelPlannedRelationshipResult>,
      IAtomicReplayAuthorizer<CancelPlannedRelationshipCommand>
{
    public async Task<CancelPlannedRelationshipResult> HandleAsync(
        CancelPlannedRelationshipCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var nowUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedRelationships(command, attempt.Persistence, nowUtc).AnyAsync(ct))
        {
            throw Unauthorized();
        }

        var mutation = await attempt.Leasing.CancelPlannedRelationshipAsync(
            command.PortfolioId,
            command.LeaseManagementId,
            command.Accesses.Select(item => new AtomicCancelPlannedAccessInput(
                item.TenantUserAccessId, item.Disposition)).ToArray(),
            command.CreatedByUserId,
            nowUtc,
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
            attempt.StageSemanticEvent(Updated(command, nameof(LeaseAgreement), agreementId,
                "Unissued Agreement draft canceled with the planned relationship."), nowUtc);
        }
        foreach (var addendumId in mutation.CanceledAddendumDraftIds)
        {
            attempt.StageSemanticEvent(Updated(command, nameof(LeaseAddendum), addendumId,
                "Unissued Addendum draft canceled with the planned relationship."), nowUtc);
        }
        foreach (var accessId in mutation.RevokedAccessIds)
        {
            attempt.StageSemanticEvent(Updated(command, nameof(TenantUserAccess), accessId,
                "Tenant access revoked under the explicit cancellation policy."), nowUtc);
        }
        if (mutation.TenantAccountId is int tenantAccountId)
        {
            attempt.StageSemanticEvent(Updated(command, nameof(TenantAccount), tenantAccountId,
                "Empty Tenant Account closed with the canceled planned relationship."), nowUtc);
        }
        attempt.StageSemanticEvent(Updated(command, nameof(LeaseManagement),
            command.LeaseManagementId, "Planned lease relationship canceled before possession."), nowUtc);
        attempt.StageOutbox(PossessionOutbox.Create(
            command.PortfolioId,
            command.DeliveryIdempotencyKey,
            nowUtc,
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
        CancelPlannedRelationshipCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var nowUtc = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedRelationships(command, persistence, nowUtc).AnyAsync(ct))
        {
            throw Unauthorized();
        }
    }

    private static IQueryable<LeaseManagement> AuthorizedRelationships(
        CancelPlannedRelationshipCommand command,
        IAtomicPersistenceSession persistence,
        DateTime nowUtc) =>
        PossessionCommandAuthorization.AuthorizedRelationships(
            persistence,
            command.PortfolioId,
            command.LeaseManagementId,
            command.UnitId,
            command.CreatedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            nowUtc);

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
