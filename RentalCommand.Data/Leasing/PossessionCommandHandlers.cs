using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class GivePossessionHandler
    : IAtomicCommandHandler<GivePossessionCommand, GivePossessionResult>,
      IAtomicReplayAuthorizer<GivePossessionCommand>
{
    public async Task<GivePossessionResult> HandleAsync(
        GivePossessionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        PossessionCommandAuthorization.ValidateShape(command.PortfolioId, command.LeaseManagementId,
            command.UnitId, command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, command.DeliveryIdempotencyKey);

        await attempt.Locking.AcquireAsync(AtomicLockResource.Unit, command.UnitId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);

        var nowUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var target = await PossessionCommandAuthorization.AuthorizedRelationships(
                attempt.Persistence, command.PortfolioId, command.LeaseManagementId, command.UnitId,
                command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
                command.ExpectedAccessRevision, nowUtc)
            .Select(relationship => new GiveTarget(
                relationship,
                relationship.TenantAccount != null && relationship.TenantAccount.ClosedAtUtc == null,
                relationship.Agreements.Any(agreement =>
                    agreement.FullyExecutedAtUtc != null
                    && agreement.ExecutedArtifactId != null
                    && agreement.VoidedAtUtc == null
                    && agreement.DraftCanceledAtUtc == null
                    && attempt.Persistence.Query<LeaseAgreementStatusProjection>().Any(status =>
                        status.PortfolioId == command.PortfolioId
                        && status.LeaseManagementId == relationship.Id
                        && status.AgreementId == agreement.Id
                        && status.IsGoverning)),
                attempt.Persistence.Query<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                    lifecycle.PortfolioId == command.PortfolioId
                    && lifecycle.LeaseManagementId == relationship.Id
                    && lifecycle.CurrentResidentCount > 0),
                attempt.Persistence.Query<LeaseManagement>().Any(other =>
                    other.PortfolioId == command.PortfolioId
                    && other.UnitId == command.UnitId
                    && other.Id != relationship.Id
                    && other.PossessionGivenAtUtc != null
                    && other.PossessionReturnedAtUtc == null),
                attempt.Persistence.Query<UnitOperationalPeriod>().Any(period =>
                    period.PortfolioId == command.PortfolioId
                    && period.UnitId == command.UnitId
                    && period.EndedAtUtc == null)))
            .SingleOrDefaultAsync(ct);

        if (target is null)
        {
            throw new UnauthorizedAccessException("The lease relationship is not authorized in the current property scope.");
        }
        if (target.Relationship.CanceledAtUtc is not null || target.Relationship.PossessionReturnedAtUtc is not null)
        {
            return Empty(GivePossessionOutcome.RelationshipNotEligible, command,
                "The lease relationship is not eligible for possession.");
        }
        if (target.Relationship.PossessionGivenAtUtc is not null)
        {
            return new(GivePossessionOutcome.AlreadyGiven, command.LeaseManagementId, command.UnitId,
                target.Relationship.PossessionGivenAtUtc, "Possession has already been given.");
        }
        if (!target.HasCurrentResident)
        {
            return Empty(GivePossessionOutcome.RelationshipNotEligible, command,
                "The lease relationship has no current resident party.");
        }
        if (!target.HasOpenAccount)
        {
            return Empty(GivePossessionOutcome.AccountNotOpen, command, "The tenant account must be open.");
        }
        if (!target.HasExecutedGoverningAgreement)
        {
            return Empty(GivePossessionOutcome.AgreementNotExecuted, command,
                "An executed governing agreement is required before possession can be given.");
        }
        if (target.HasOtherPossession || target.HasOpenOperationalPeriod)
        {
            return Empty(GivePossessionOutcome.UnitUnavailable, command,
                "The unit has conflicting possession or an open operational period.");
        }

        target.Relationship.PossessionGivenAtUtc = nowUtc;
        target.Relationship.UpdatedAtUtc = nowUtc;
        target.Relationship.RowVersion = Guid.NewGuid();
        attempt.BindSemanticAudit(target.Relationship, Updated(command.PortfolioId, command.LeaseManagementId,
            command.CreatedByUserId, "Possession given under an executed governing agreement."));
        attempt.StageOutbox(PossessionOutbox.Create(command.PortfolioId, command.DeliveryIdempotencyKey,
            nowUtc, "possession-given", nameof(LeaseManagement), command.LeaseManagementId,
            command.LeaseManagementId, command.UnitId));
        await attempt.FlushBusinessAsync(ct);

        return new(GivePossessionOutcome.Given, command.LeaseManagementId, command.UnitId, nowUtc, null);
    }

    public async Task AuthorizeReplayAsync(GivePossessionCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        PossessionCommandAuthorization.ValidateShape(command.PortfolioId, command.LeaseManagementId,
            command.UnitId, command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, command.DeliveryIdempotencyKey);
        var nowUtc = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await PossessionCommandAuthorization.AuthorizedRelationships(persistence, command.PortfolioId,
                command.LeaseManagementId, command.UnitId, command.CreatedByUserId, command.AuthSessionId,
                command.AccessContextId, command.ExpectedAccessRevision, nowUtc).AnyAsync(ct))
        {
            throw new UnauthorizedAccessException("The lease relationship is not authorized in the current property scope.");
        }
    }

    private static GivePossessionResult Empty(GivePossessionOutcome outcome, GivePossessionCommand command,
        string error) => new(outcome, command.LeaseManagementId, command.UnitId, null, error);

    private static AtomicSemanticAudit Updated(int portfolioId, int relationshipId, int userId, string reason) =>
        new(portfolioId, nameof(LeaseManagement), relationshipId, AuditLogOperation.Updated,
            UserId: userId, ChangeReason: reason);

    private sealed record GiveTarget(LeaseManagement Relationship, bool HasOpenAccount,
        bool HasExecutedGoverningAgreement, bool HasCurrentResident, bool HasOtherPossession,
        bool HasOpenOperationalPeriod);
}

public sealed class ReturnPossessionHandler
    : IAtomicCommandHandler<ReturnPossessionCommand, ReturnPossessionResult>,
      IAtomicReplayAuthorizer<ReturnPossessionCommand>
{
    public async Task<ReturnPossessionResult> HandleAsync(
        ReturnPossessionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Unit, command.UnitId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);

        var nowUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var target = await PossessionCommandAuthorization.AuthorizedRelationships(
                attempt.Persistence, command.PortfolioId, command.LeaseManagementId, command.UnitId,
                command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
                command.ExpectedAccessRevision, nowUtc)
            .Select(relationship => new ReturnTarget(
                relationship,
                attempt.Persistence.Query<UnitOperationalPeriod>().Any(period =>
                    period.PortfolioId == command.PortfolioId
                    && period.UnitId == command.UnitId
                    && period.Type == UnitOperationalPeriodType.Turnover
                    && period.EndedAtUtc == null)))
            .SingleOrDefaultAsync(ct);

        if (target is null)
        {
            throw new UnauthorizedAccessException("The lease relationship is not authorized in the current property scope.");
        }
        if (target.Relationship.PossessionReturnedAtUtc is not null)
        {
            return new(ReturnPossessionOutcome.AlreadyReturned, command.LeaseManagementId, command.UnitId,
                null, target.Relationship.PossessionReturnedAtUtc, "Possession has already been returned.");
        }
        if (target.Relationship.PossessionGivenAtUtc is null)
        {
            return Empty(ReturnPossessionOutcome.PossessionNotGiven, command,
                "Possession cannot be returned before it has been given.");
        }
        if (target.HasOpenTurnover)
        {
            return Empty(ReturnPossessionOutcome.TurnoverAlreadyOpen, command,
                "The unit already has an open turnover period.");
        }

        // Query 3 is a single bounded relationship-owned graph. Include is intentional: it produces
        // one SQL statement and avoids a per-party or per-access query.
        var currentParties = await attempt.Persistence.Query<LeaseManagementParty>()
            .Where(party => party.PortfolioId == command.PortfolioId
                && party.LeaseManagementId == command.LeaseManagementId
                && party.EffectiveFrom <= command.EffectiveOn
                && (party.EffectiveThrough == null || party.EffectiveThrough >= command.EffectiveOn))
            .Include(party => party.UserAccesses.Where(access => access.RevokedAtUtc == null))
            .ToListAsync(ct);

        var partyDispositionById = command.Parties.ToDictionary(item => item.LeaseManagementPartyId);
        if (currentParties.Count != partyDispositionById.Count
            || currentParties.Any(party => !partyDispositionById.ContainsKey(party.Id))
            || currentParties.Any(party => partyDispositionById[party.Id].Disposition == ReturnPartyDisposition.RetainGuarantor
                && party.Role != LeaseManagementPartyRole.Guarantor))
        {
            return Empty(ReturnPossessionOutcome.InvalidPartyDisposition, command,
                "Every current party must have one disposition, and only a guarantor may remain.");
        }

        var activeAccesses = currentParties.SelectMany(party => party.UserAccesses).ToArray();
        var accessDispositionById = command.Accesses.ToDictionary(item => item.TenantUserAccessId);
        if (activeAccesses.Length != accessDispositionById.Count
            || activeAccesses.Any(access => !accessDispositionById.ContainsKey(access.Id)))
        {
            return Empty(ReturnPossessionOutcome.InvalidAccessDisposition, command,
                "Every active tenant access must have one explicit disposition.");
        }

        foreach (var party in currentParties)
        {
            if (partyDispositionById[party.Id].Disposition == ReturnPartyDisposition.EndMembership)
            {
                party.EffectiveThrough = command.EffectiveOn;
                attempt.BindSemanticAudit(party, new AtomicSemanticAudit(command.PortfolioId,
                    nameof(LeaseManagementParty), party.Id, AuditLogOperation.Updated,
                    UserId: command.CreatedByUserId, ChangeReason: "Party membership ended when possession returned."));
            }
        }
        foreach (var access in activeAccesses)
        {
            if (accessDispositionById[access.Id].Disposition == ReturnAccessDisposition.RevokeNow)
            {
                access.RevokedAtUtc = nowUtc;
                access.RevokedByUserId = command.CreatedByUserId;
                attempt.BindSemanticAudit(access, new AtomicSemanticAudit(command.PortfolioId,
                    nameof(TenantUserAccess), access.Id, AuditLogOperation.Updated,
                    UserId: command.CreatedByUserId, ChangeReason: "Tenant access revoked when possession returned."));
            }
        }

        target.Relationship.PossessionReturnedAtUtc = nowUtc;
        target.Relationship.UpdatedAtUtc = nowUtc;
        target.Relationship.RowVersion = Guid.NewGuid();
        var turnover = new UnitOperationalPeriod
        {
            PortfolioId = command.PortfolioId,
            PropertyId = target.Relationship.PropertyId,
            UnitId = command.UnitId,
            Type = UnitOperationalPeriodType.Turnover,
            StartedAtUtc = nowUtc,
            SourceLeaseManagementId = command.LeaseManagementId,
            Reason = command.TurnoverReason.Trim(),
            CreatedAtUtc = nowUtc,
            CreatedByUserId = command.CreatedByUserId,
        };
        attempt.Persistence.Add(turnover);
        attempt.BindSemanticAudit(target.Relationship, new AtomicSemanticAudit(command.PortfolioId,
            nameof(LeaseManagement), command.LeaseManagementId, AuditLogOperation.Updated,
            UserId: command.CreatedByUserId, ChangeReason: "Possession returned; tenant account remains open."));
        attempt.BindSemanticAudit(turnover, new AtomicSemanticAudit(command.PortfolioId,
            nameof(UnitOperationalPeriod), 0, AuditLogOperation.Created,
            UserId: command.CreatedByUserId, ChangeReason: "Turnover opened after possession return."));
        attempt.StageOutbox(PossessionOutbox.Create(command.PortfolioId, command.DeliveryIdempotencyKey,
            nowUtc, "possession-returned", nameof(LeaseManagement), command.LeaseManagementId,
            command.LeaseManagementId, command.UnitId));
        await attempt.FlushBusinessAsync(ct);

        return new(ReturnPossessionOutcome.Returned, command.LeaseManagementId, command.UnitId,
            turnover.Id, nowUtc, null);
    }

    public async Task AuthorizeReplayAsync(ReturnPossessionCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var nowUtc = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await PossessionCommandAuthorization.AuthorizedRelationships(persistence, command.PortfolioId,
                command.LeaseManagementId, command.UnitId, command.CreatedByUserId, command.AuthSessionId,
                command.AccessContextId, command.ExpectedAccessRevision, nowUtc).AnyAsync(ct))
        {
            throw new UnauthorizedAccessException("The lease relationship is not authorized in the current property scope.");
        }
    }

    private static void Validate(ReturnPossessionCommand command)
    {
        PossessionCommandAuthorization.ValidateShape(command.PortfolioId, command.LeaseManagementId,
            command.UnitId, command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, command.DeliveryIdempotencyKey);
        if (command.EffectiveOn == default || string.IsNullOrWhiteSpace(command.TurnoverReason)
            || command.TurnoverReason.Length > 500
            || command.Parties.Count == 0
            || command.Parties.Select(item => item.LeaseManagementPartyId).Distinct().Count() != command.Parties.Count
            || command.Accesses.Select(item => item.TenantUserAccessId).Distinct().Count() != command.Accesses.Count
            || command.Parties.Any(item => item.LeaseManagementPartyId <= 0 || !Enum.IsDefined(item.Disposition))
            || command.Accesses.Any(item => item.TenantUserAccessId <= 0 || !Enum.IsDefined(item.Disposition)))
        {
            throw new ArgumentException("Return possession dispositions and turnover reason are invalid.");
        }
    }

    private static ReturnPossessionResult Empty(ReturnPossessionOutcome outcome,
        ReturnPossessionCommand command, string error) =>
        new(outcome, command.LeaseManagementId, command.UnitId, null, null, error);

    private sealed record ReturnTarget(LeaseManagement Relationship, bool HasOpenTurnover);
}

public sealed class CompleteTurnoverHandler
    : IAtomicCommandHandler<CompleteTurnoverCommand, CompleteTurnoverResult>,
      IAtomicReplayAuthorizer<CompleteTurnoverCommand>
{
    public async Task<CompleteTurnoverResult> HandleAsync(CompleteTurnoverCommand command,
        IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Unit, command.UnitId, ct);
        var nowUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var authorizedUnits = PossessionCommandAuthorization.AuthorizedUnits(attempt.Persistence,
            command.PortfolioId, command.UnitId, command.CreatedByUserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision, nowUtc);
        var period = await attempt.Persistence.Query<UnitOperationalPeriod>()
            .Where(candidate => candidate.Id == command.TurnoverPeriodId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.UnitId == command.UnitId
                && candidate.Type == UnitOperationalPeriodType.Turnover
                && authorizedUnits.Any(unit => unit.Id == candidate.UnitId
                    && unit.PropertyId == candidate.PropertyId))
            .SingleOrDefaultAsync(ct);
        if (period is null)
        {
            return new(CompleteTurnoverOutcome.TurnoverNotFound, command.UnitId,
                command.TurnoverPeriodId, null, "The turnover period was not found in the authorized unit.");
        }
        if (period.EndedAtUtc is not null)
        {
            return new(CompleteTurnoverOutcome.AlreadyCompleted, command.UnitId,
                command.TurnoverPeriodId, period.EndedAtUtc, "Turnover has already been completed.");
        }

        period.EndedAtUtc = nowUtc;
        attempt.BindSemanticAudit(period, new AtomicSemanticAudit(command.PortfolioId,
            nameof(UnitOperationalPeriod), period.Id, AuditLogOperation.Updated,
            UserId: command.CreatedByUserId, ChangeReason: "Turnover completed."));
        attempt.StageOutbox(PossessionOutbox.Create(command.PortfolioId, command.DeliveryIdempotencyKey,
            nowUtc, "turnover-completed", nameof(UnitOperationalPeriod), period.Id,
            period.SourceLeaseManagementId, command.UnitId));
        await attempt.FlushBusinessAsync(ct);
        return new(CompleteTurnoverOutcome.Completed, command.UnitId, period.Id, nowUtc, null);
    }

    public async Task AuthorizeReplayAsync(CompleteTurnoverCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var nowUtc = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await PossessionCommandAuthorization.AuthorizedUnits(persistence, command.PortfolioId,
                command.UnitId, command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
                command.ExpectedAccessRevision, nowUtc).AnyAsync(ct))
        {
            throw new UnauthorizedAccessException("The unit is not authorized in the current property scope.");
        }
    }

    private static void Validate(CompleteTurnoverCommand command)
    {
        PossessionCommandAuthorization.ValidateShape(command.PortfolioId, 1, command.UnitId,
            command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, command.DeliveryIdempotencyKey);
        if (command.TurnoverPeriodId <= 0)
        {
            throw new ArgumentException("A turnover period id is required.");
        }
    }
}

internal static class PossessionCommandAuthorization
{
    internal static IQueryable<LeaseManagement> AuthorizedRelationships(IAtomicPersistenceSession persistence,
        int portfolioId, int relationshipId, int unitId, int userId, Guid sessionId,
        int accessContextId, long accessRevision, DateTime nowUtc)
    {
        var memberships = AuthorizedMemberships(persistence, portfolioId, userId, sessionId,
            accessContextId, accessRevision, nowUtc);
        var assignments = EffectiveAssignments(persistence, portfolioId, nowUtc);
        return persistence.Query<LeaseManagement>().Where(relationship =>
            relationship.Id == relationshipId && relationship.PortfolioId == portfolioId
            && relationship.UnitId == unitId && relationship.Unit != null
            && relationship.Unit.PortfolioId == portfolioId
            && relationship.Property != null && relationship.Property.PortfolioId == portfolioId
            && memberships.Any(membership => assignments.Any(assignment =>
                assignment.WorkspaceMembershipId == membership.Id
                && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(scope =>
                            scope.PropertyId == relationship.PropertyId && scope.PortfolioId == portfolioId)))
                && (assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage)
                    || assignment.RoleProfile.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingOnboardingManage)))));
    }

    internal static IQueryable<Unit> AuthorizedUnits(IAtomicPersistenceSession persistence,
        int portfolioId, int unitId, int userId, Guid sessionId, int accessContextId,
        long accessRevision, DateTime nowUtc)
    {
        var memberships = AuthorizedMemberships(persistence, portfolioId, userId, sessionId,
            accessContextId, accessRevision, nowUtc);
        var assignments = EffectiveAssignments(persistence, portfolioId, nowUtc);
        return persistence.Query<Unit>().Where(unit => unit.Id == unitId && unit.PortfolioId == portfolioId
            && unit.Property != null && unit.Property.PortfolioId == portfolioId
            && memberships.Any(membership => assignments.Any(assignment =>
                assignment.WorkspaceMembershipId == membership.Id
                && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(scope =>
                            scope.PropertyId == unit.PropertyId && scope.PortfolioId == portfolioId)))
                && (assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage)
                    || assignment.RoleProfile.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingOnboardingManage)))));
    }

    private static IQueryable<WorkspaceMembership> AuthorizedMemberships(
        IAtomicPersistenceSession persistence, int portfolioId, int userId, Guid sessionId,
        int accessContextId, long accessRevision, DateTime nowUtc)
        => persistence.Query<WorkspaceMembership>().Where(membership =>
            persistence.Query<AuthSession>().Any(session => session.Id == sessionId
                && session.UserId == userId && session.ActiveAccessContextId == accessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > nowUtc)
            && persistence.Query<WorkspaceAccessContext>().Any(context => context.Id == accessContextId
                && context.UserId == userId && context.PortfolioId == portfolioId
                && context.AccessRevision == accessRevision && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
                && membership.AccessContextId == accessContextId && membership.PortfolioId == portfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= nowUtc
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > nowUtc));

    private static IQueryable<MembershipRoleAssignment> EffectiveAssignments(
        IAtomicPersistenceSession persistence, int portfolioId, DateTime nowUtc) =>
        persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == portfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= nowUtc
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > nowUtc));

    internal static void ValidateShape(int portfolioId, int relationshipId, int unitId, int userId,
        Guid sessionId, int accessContextId, long accessRevision, string deliveryIdempotencyKey)
    {
        if (portfolioId <= 0 || relationshipId <= 0 || unitId <= 0 || userId <= 0
            || sessionId == Guid.Empty || accessContextId <= 0 || accessRevision <= 0
            || string.IsNullOrWhiteSpace(deliveryIdempotencyKey) || deliveryIdempotencyKey.Length > 200)
        {
            throw new ArgumentException("Portfolio, relationship, unit, actor, access, and delivery ids are required.");
        }
    }
}

internal static class PossessionOutbox
{
    internal static OutboxMessage Create(int portfolioId, string idempotencyKey, DateTime nowUtc,
        string eventName, string entityType, int entityId, int? relationshipId, int unitId) => new()
    {
        PortfolioId = portfolioId,
        MessageType = "data-update",
        Payload = JsonSerializer.Serialize(new
        {
            entityType,
            entityId,
            data = new { eventName, leaseManagementId = relationshipId, unitId },
        }),
        IdempotencyKey = idempotencyKey,
        CreatedAtUtc = nowUtc,
        NextAttemptAtUtc = nowUtc,
    };
}
