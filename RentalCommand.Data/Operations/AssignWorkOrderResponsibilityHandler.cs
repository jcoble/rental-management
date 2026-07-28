using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;

namespace RentalCommand.Data.Operations;

public sealed class AssignWorkOrderResponsibilityHandler
    : IAtomicCommandHandler<AssignWorkOrderResponsibilityCommand, AssignWorkOrderResponsibilityResult>,
      IAtomicReplayAuthorizer<AssignWorkOrderResponsibilityCommand>
{
    public async Task<AssignWorkOrderResponsibilityResult> HandleAsync(
        AssignWorkOrderResponsibilityCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkOrder, command.WorkOrderId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.ActorAccessContextId, ct);
        var securityNowUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = attempt.BusinessNowUtc;
        var workOrder = await AuthorizeActorAndLoadWorkOrderAsync(command, attempt.Persistence, securityNowUtc, ct);

        var target = await attempt.Persistence.Query<MembershipRoleAssignment>()
            .AsNoTracking()
            .Where(assignment =>
                assignment.Id == command.MembershipRoleAssignmentId &&
                assignment.WorkspaceMembershipId == command.WorkspaceMembershipId &&
                assignment.PortfolioId == command.PortfolioId &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.SuspendedAtUtc == null &&
                assignment.RevokedAtUtc == null &&
                assignment.EffectiveFromUtc <= securityNowUtc &&
                (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc) &&
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                assignment.RoleProfile!.Key == RoleProfileKeys.MaintenanceTechnician &&
                assignment.RoleProfile.Capabilities.Any(capability =>
                    capability.CapabilityDefinition!.Key == CapabilityKeys.AssignedWorkRead &&
                    capability.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.WorkOrder) &&
                assignment.WorkspaceMembership!.Status == WorkspaceMembershipStatus.Active &&
                assignment.WorkspaceMembership.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.RevokedAtUtc == null &&
                assignment.WorkspaceMembership.EffectiveFromUtc <= securityNowUtc &&
                (assignment.WorkspaceMembership.EffectiveToUtc == null ||
                 assignment.WorkspaceMembership.EffectiveToUtc > securityNowUtc) &&
                assignment.WorkspaceMembership.AccessContext!.Status == WorkspaceAccessContextStatus.Active &&
                assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null)
            .Select(assignment => new
            {
                AccessContextId = assignment.WorkspaceMembership!.AccessContextId,
                assignment.WorkspaceMembership.AccessContext!.AccessRevision,
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException(
                "The selected member does not have this exact effective Maintenance Technician assignment.");

        var currentResponsibilities = attempt.Persistence.Query<WorkOrderResponsibility>()
            .Where(responsibility =>
                responsibility.WorkOrderId == command.WorkOrderId &&
                responsibility.PortfolioId == command.PortfolioId &&
                responsibility.EffectiveToUtc == null);
        var currentPrimaryId = await currentResponsibilities
            .Where(responsibility => responsibility.Kind == WorkOrderResponsibilityKind.Primary)
            .Select(responsibility => (Guid?)responsibility.Id)
            .SingleOrDefaultAsync(ct);
        if (currentPrimaryId != command.ExpectedCurrentPrimaryResponsibilityId)
            throw new DomainValidationException("The current primary responsibility changed; refresh before retrying.");

        if (await currentResponsibilities.AnyAsync(responsibility =>
                responsibility.WorkspaceMembershipId == command.WorkspaceMembershipId &&
                responsibility.Kind == command.Kind, ct))
            throw new DomainValidationException("This member already holds that current responsibility.");

        var closingResponsibilities = currentResponsibilities.Where(responsibility =>
            responsibility.WorkspaceMembershipId == command.WorkspaceMembershipId ||
            (command.Kind == WorkOrderResponsibilityKind.Primary &&
             responsibility.Kind == WorkOrderResponsibilityKind.Primary));
        var toClose = await closingResponsibilities
            .OrderBy(responsibility => responsibility.Id)
            .ToListAsync(ct);
        var affectedContextIds = await closingResponsibilities
            .Select(responsibility => responsibility.WorkspaceMembership!.AccessContextId)
            .Concat(attempt.Persistence.Query<WorkspaceAccessContext>()
                .Where(context => context.Id == target.AccessContextId &&
                                  context.PortfolioId == command.PortfolioId)
                .Select(context => context.Id))
            .Distinct()
            .OrderBy(contextId => contextId)
            .ToArrayAsync(ct);
        var expectations = command.AccessRevisionExpectations
            .OrderBy(item => item.AccessContextId)
            .ToArray();
        if (!affectedContextIds.SequenceEqual(expectations.Select(item => item.AccessContextId)))
            throw new DomainValidationException("Expected access revisions must exactly cover affected assignees.");

        foreach (var contextId in affectedContextIds.Where(id => id != command.ActorAccessContextId))
            await attempt.Locking.AcquireAsync(AtomicLockResource.WorkspaceAccessContext, contextId, ct);

        var contexts = await attempt.Persistence.Query<WorkspaceAccessContext>()
            .Where(context => affectedContextIds.Contains(context.Id) &&
                              context.PortfolioId == command.PortfolioId)
            .OrderBy(context => context.Id)
            .ToListAsync(ct);
        if (contexts.Count != affectedContextIds.Length)
            throw new AccessContextUnavailableException();
        foreach (var context in contexts)
        {
            var expectation = expectations.Single(item => item.AccessContextId == context.Id);
            if (context.AccessRevision != expectation.ExpectedRevision)
                throw new StaleAccessRevisionException(expectation.ExpectedRevision, context.AccessRevision);
            context.UpdatedAtUtc = securityNowUtc;
            context.AdvanceRevision(expectation.ExpectedRevision);
        }

        foreach (var closing in toClose)
        {
            closing.EffectiveToUtc = businessNowUtc;
            closing.EndedAtUtc = businessNowUtc;
            closing.EndedByUserId = command.ActorUserId;
            closing.EndedByAccessContextId = command.ActorAccessContextId;
            closing.EndedReason = command.Reason.Trim();
        }

        var responsibility = new WorkOrderResponsibility
        {
            Id = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            PropertyId = workOrder.PropertyId,
            WorkOrderId = workOrder.Id,
            WorkspaceMembershipId = command.WorkspaceMembershipId,
            MembershipRoleAssignmentId = command.MembershipRoleAssignmentId,
            Kind = command.Kind,
            EffectiveFromUtc = businessNowUtc,
            AssignedByUserId = command.ActorUserId,
            AssignedByAccessContextId = command.ActorAccessContextId,
            AssignedReason = command.Reason.Trim(),
            AssignedAtUtc = businessNowUtc,
        };
        attempt.Persistence.Add(responsibility);
        attempt.UseDatabaseWallClockForAudit(businessNowUtc);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(WorkOrderResponsibility),
            command.WorkOrderId,
            AuditLogOperation.Updated,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                responsibility.Id,
                command.WorkOrderId,
                command.WorkspaceMembershipId,
                command.MembershipRoleAssignmentId,
                command.Kind,
                ClosedResponsibilityIds = toClose.Select(item => item.Id),
            }),
            ChangeReason: command.Reason.Trim()), businessNowUtc);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(WorkOrder),
                entityId = command.WorkOrderId,
                data = new { responsibility.Id, command.WorkspaceMembershipId, command.Kind },
            }),
            IdempotencyKey = $"work-order-responsibility:{command.DeliveryIdempotencyKey}",
            CreatedAtUtc = businessNowUtc,
            NextAttemptAtUtc = businessNowUtc,
        });

        return new AssignWorkOrderResponsibilityResult(
            responsibility.Id,
            command.WorkOrderId,
            command.WorkspaceMembershipId,
            command.MembershipRoleAssignmentId,
            command.Kind,
            businessNowUtc,
            contexts
                .Select(context => new WorkspaceAccessRevisionExpectation(context.Id, context.AccessRevision))
                .ToArray());
    }

    public async Task AuthorizeReplayAsync(
        AssignWorkOrderResponsibilityCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        _ = await AuthorizeActorAndLoadWorkOrderAsync(command, persistence, now, ct);
    }

    private static async Task<WorkOrder> AuthorizeActorAndLoadWorkOrderAsync(
        AssignWorkOrderResponsibilityCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        var sessions = persistence.Query<AuthSession>().AsNoTracking();
        var contexts = persistence.Query<WorkspaceAccessContext>().AsNoTracking();
        var workOrder = await persistence.Query<WorkOrder>()
            .Where(item => item.Id == command.WorkOrderId && item.PortfolioId == command.PortfolioId)
            .Where(item =>
                contexts.Any(context =>
                    context.Id == command.ActorAccessContextId &&
                    context.UserId == command.ActorUserId &&
                    context.PortfolioId == item.PortfolioId &&
                    context.AccessRevision == command.ActorAccessRevision &&
                    context.Status == WorkspaceAccessContextStatus.Active &&
                    context.SuspendedAtUtc == null &&
                    context.RevokedAtUtc == null &&
                    sessions.Any(session =>
                        session.Id == command.ActorAuthSessionId &&
                        session.UserId == command.ActorUserId &&
                        session.ActiveAccessContextId == context.Id &&
                        session.Status == AuthSessionStatus.Active &&
                        session.RevokedAtUtc == null &&
                        session.ExpiresAtUtc > now) &&
                    context.Membership != null &&
                    context.Membership.Status == WorkspaceMembershipStatus.Active &&
                    context.Membership.SuspendedAtUtc == null &&
                    context.Membership.RevokedAtUtc == null &&
                    context.Membership.EffectiveFromUtc <= now &&
                    (context.Membership.EffectiveToUtc == null || context.Membership.EffectiveToUtc > now) &&
                    context.Membership.RoleAssignments.Any(assignment =>
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null &&
                        assignment.EffectiveFromUtc <= now &&
                        (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now) &&
                        assignment.RoleProfile!.Capabilities.Any(capability =>
                            capability.CapabilityDefinition!.Key ==
                                CapabilityKeys.ResponsibilityAssignExistingMember &&
                            capability.CapabilityDefinition.AuthorizationTargetKind ==
                                CapabilityAuthorizationTargetKind.Property) &&
                        (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                         (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                          assignment.SelectedProperties.Any(selected =>
                              selected.PropertyId == item.PropertyId &&
                              selected.PortfolioId == item.PortfolioId))))
                )
            )
            .SingleOrDefaultAsync(ct);
        return workOrder ?? throw new UnauthorizedAccessException(
            "The active assignment cannot assign responsibility for this work order.");
    }

    private static void Validate(AssignWorkOrderResponsibilityCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0 ||
            command.ActorAuthSessionId == Guid.Empty || command.ActorAccessContextId <= 0 ||
            command.ActorAccessRevision <= 0 || command.WorkOrderId <= 0 ||
            command.WorkspaceMembershipId <= 0 || command.MembershipRoleAssignmentId <= 0 ||
            !Enum.IsDefined(command.Kind) || string.IsNullOrWhiteSpace(command.Reason) ||
            command.Reason.Trim().Length > 1000 ||
            string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new DomainValidationException("A complete, valid responsibility assignment is required.");
        if (command.AccessRevisionExpectations.Length == 0 ||
            command.AccessRevisionExpectations.GroupBy(item => item.AccessContextId).Any(group => group.Count() != 1))
            throw new DomainValidationException("Affected access revision expectations must be unique.");
    }

}
