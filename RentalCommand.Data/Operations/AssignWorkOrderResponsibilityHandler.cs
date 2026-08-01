using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Operations;

public sealed class AssignWorkOrderResponsibilityHandler
    : IAtomicCommandHandler<AssignWorkOrderResponsibilityCommand, AssignWorkOrderResponsibilityResult>
{
    private readonly RentalCommandDbContext _db;
    private readonly WorkOrderResponsibilityAccessRevisionGuard _accessRevisionGuard;

    public AssignWorkOrderResponsibilityHandler(
        RentalCommandDbContext db,
        WorkOrderResponsibilityAccessRevisionGuard accessRevisionGuard)
    {
        _db = db;
        _accessRevisionGuard = accessRevisionGuard;
    }

    public async Task<AssignWorkOrderResponsibilityResult> HandleAsync(
        AssignWorkOrderResponsibilityCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("WorkOrder", command.WorkOrderId, ct);
        await context.AcquireLockAsync(
            "WorkspaceAccessContext", command.ActorAccessContextId, ct);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        var workOrder = await AuthorizeActorAndLoadWorkOrderAsync(
            command, _db, businessNowUtc, securityNowUtc, ct);

        var target = await _db.Set<MembershipRoleAssignment>()
            .AsNoTracking()
            .Where(assignment =>
                assignment.Id == command.MembershipRoleAssignmentId &&
                assignment.WorkspaceMembershipId == command.WorkspaceMembershipId &&
                assignment.PortfolioId == command.PortfolioId &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.SuspendedAtUtc == null &&
                assignment.RevokedAtUtc == null &&
                assignment.EffectiveFromUtc <= businessNowUtc &&
                (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > businessNowUtc) &&
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                assignment.RoleProfile!.Key == RoleProfileKeys.MaintenanceTechnician &&
                assignment.RoleProfile.Capabilities.Any(capability =>
                    capability.CapabilityDefinition!.Key == CapabilityKeys.AssignedWorkRead &&
                    capability.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.WorkOrder) &&
                assignment.WorkspaceMembership!.Status == WorkspaceMembershipStatus.Active &&
                assignment.WorkspaceMembership.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.RevokedAtUtc == null &&
                assignment.WorkspaceMembership.EffectiveFromUtc <= businessNowUtc &&
                (assignment.WorkspaceMembership.EffectiveToUtc == null ||
                 assignment.WorkspaceMembership.EffectiveToUtc > businessNowUtc) &&
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

        var currentResponsibilities = _db.Set<WorkOrderResponsibility>()
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
            .Concat(_db.Set<WorkspaceAccessContext>()
                .Where(accessContext => accessContext.Id == target.AccessContextId &&
                                        accessContext.PortfolioId == command.PortfolioId)
                .Select(accessContext => accessContext.Id))
            .Distinct()
            .OrderBy(contextId => contextId)
            .ToArrayAsync(ct);
        var expectations = command.AccessRevisionExpectations
            .OrderBy(item => item.AccessContextId)
            .ToArray();
        if (!affectedContextIds.SequenceEqual(expectations.Select(item => item.AccessContextId)))
            throw new DomainValidationException("Expected access revisions must exactly cover affected assignees.");

        foreach (var contextId in affectedContextIds.Where(id => id != command.ActorAccessContextId))
            await context.AcquireLockAsync("WorkspaceAccessContext", contextId, ct);

        var contexts = await _db.Set<WorkspaceAccessContext>()
            .Where(accessContext => affectedContextIds.Contains(accessContext.Id) &&
                                    accessContext.PortfolioId == command.PortfolioId)
            .OrderBy(accessContext => accessContext.Id)
            .ToListAsync(ct);
        if (contexts.Count != affectedContextIds.Length)
            throw new AccessContextUnavailableException();
        foreach (var accessContext in contexts)
        {
            var expectation = expectations.Single(item => item.AccessContextId == accessContext.Id);
            if (accessContext.AccessRevision != expectation.ExpectedRevision)
                throw new StaleAccessRevisionException(expectation.ExpectedRevision, accessContext.AccessRevision);
            accessContext.UpdatedAtUtc = securityNowUtc;
            accessContext.AdvanceRevision(expectation.ExpectedRevision);
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
        _db.Add(responsibility);
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        context.StageSemanticEvent(new AtomicSemanticAudit(
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
        context.StageOutbox(new OutboxMessage
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

        await _accessRevisionGuard.ValidatePendingMutationAsync(
            _db, command.AccessRevisionExpectations, ct);
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
        AssignWorkOrderResponsibilityCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        _ = await AuthorizeActorAndLoadWorkOrderAsync(
            command, _db, command.BusinessNowUtc, securityNowUtc, ct);
    }

    private static async Task<WorkOrder> AuthorizeActorAndLoadWorkOrderAsync(
        AssignWorkOrderResponsibilityCommand command,
        RentalCommandDbContext db,
        DateTime businessNowUtc,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        var sessions = db.Set<AuthSession>().AsNoTracking();
        var contexts = db.Set<WorkspaceAccessContext>().AsNoTracking();
        var workOrder = await db.Set<WorkOrder>()
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
                        session.ExpiresAtUtc > securityNowUtc) &&
                    context.Membership != null &&
                    context.Membership.Status == WorkspaceMembershipStatus.Active &&
                    context.Membership.SuspendedAtUtc == null &&
                    context.Membership.RevokedAtUtc == null &&
                    context.Membership.EffectiveFromUtc <= businessNowUtc &&
                    (context.Membership.EffectiveToUtc == null || context.Membership.EffectiveToUtc > businessNowUtc) &&
                    context.Membership.RoleAssignments.Any(assignment =>
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null &&
                        assignment.EffectiveFromUtc <= businessNowUtc &&
                        (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > businessNowUtc) &&
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
            command.BusinessNowUtc == default ||
            string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new DomainValidationException("A complete, valid responsibility assignment is required.");
        if (command.AccessRevisionExpectations.Length == 0 ||
            command.AccessRevisionExpectations.GroupBy(item => item.AccessContextId).Any(group => group.Count() != 1))
            throw new DomainValidationException("Affected access revision expectations must be unique.");
    }

}
