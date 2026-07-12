using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Makes one typed resource decision in SQL. Capability and scope predicates remain on the same
/// assignment row, while the target record itself is joined to the active workspace in that same
/// statement. A missing or mismatched target never degrades to capability-only authorization.
/// </summary>
public sealed class WorkspaceAuthorizationEvaluator : IWorkspaceAuthorizationEvaluator
{
    private readonly RentalCommandDbContext _db;

    public WorkspaceAuthorizationEvaluator(RentalCommandDbContext db) => _db = db;

    public Task<bool> HasCapabilityAsync(
        ActiveAccessContext accessContext,
        string capabilityKey,
        WorkspaceAuthorizationTarget? target,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);
        if (target is null || target.PortfolioId != accessContext.PortfolioId)
        {
            return Task.FromResult(false);
        }

        return target switch
        {
            WorkspaceCapabilityAuthorizationTarget workspace =>
                HasWorkspaceCapabilityAsync(accessContext, capabilityKey, workspace, utcNow, cancellationToken),
            PropertyCapabilityAuthorizationTarget property =>
                HasPropertyCapabilityAsync(accessContext, capabilityKey, property, utcNow, cancellationToken),
            UnitCapabilityAuthorizationTarget unit =>
                HasUnitCapabilityAsync(accessContext, capabilityKey, unit, utcNow, cancellationToken),
            RentalApplicationCapabilityAuthorizationTarget application =>
                HasApplicationCapabilityAsync(accessContext, capabilityKey, application, utcNow, cancellationToken),
            WorkOrderCapabilityAuthorizationTarget => DenyAssignedWorkUntilResponsibilityExists(),
            _ => Task.FromResult(false),
        };
    }

    private Task<bool> HasWorkspaceCapabilityAsync(
        ActiveAccessContext accessContext,
        string capabilityKey,
        WorkspaceCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken) =>
        EffectiveAssignments(
                accessContext,
                capabilityKey,
                CapabilityAuthorizationTargetKind.Workspace,
                utcNow)
            .AnyAsync(assignment =>
                assignment.PortfolioId == target.PortfolioId &&
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties,
                cancellationToken);

    private Task<bool> HasPropertyCapabilityAsync(
        ActiveAccessContext accessContext,
        string capabilityKey,
        PropertyCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var assignments = EffectiveAssignments(
            accessContext,
            capabilityKey,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);

        return _db.Properties.AsNoTracking().AnyAsync(property =>
                property.Id == target.PropertyId &&
                property.PortfolioId == target.PortfolioId &&
                property.PortfolioId == accessContext.PortfolioId &&
                assignments.Any(assignment =>
                    assignment.PortfolioId == property.PortfolioId &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      assignment.SelectedProperties.Any(scope =>
                          scope.PropertyId == property.Id &&
                          scope.PortfolioId == property.PortfolioId)))),
            cancellationToken);
    }

    private Task<bool> HasUnitCapabilityAsync(
        ActiveAccessContext accessContext,
        string capabilityKey,
        UnitCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var assignments = EffectiveAssignments(
            accessContext,
            capabilityKey,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);

        return _db.Units.AsNoTracking().AnyAsync(unit =>
                unit.Id == target.UnitId &&
                unit.PortfolioId == target.PortfolioId &&
                unit.PortfolioId == accessContext.PortfolioId &&
                _db.Properties.AsNoTracking().Any(property =>
                    property.Id == unit.PropertyId &&
                    property.PortfolioId == unit.PortfolioId &&
                    assignments.Any(assignment =>
                        assignment.PortfolioId == property.PortfolioId &&
                        (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                         (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                          assignment.SelectedProperties.Any(scope =>
                              scope.PropertyId == property.Id &&
                              scope.PortfolioId == property.PortfolioId))))),
            cancellationToken);
    }

    private Task<bool> HasApplicationCapabilityAsync(
        ActiveAccessContext accessContext,
        string capabilityKey,
        RentalApplicationCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var assignments = EffectiveAssignments(
            accessContext,
            capabilityKey,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);

        return _db.RentalApplications.AsNoTracking().AnyAsync(application =>
                application.Id == target.ApplicationId &&
                application.PortfolioId == target.PortfolioId &&
                application.PortfolioId == accessContext.PortfolioId &&
                application.PropertyId != null &&
                _db.Properties.AsNoTracking().Any(property =>
                    property.Id == application.PropertyId.Value &&
                    property.PortfolioId == application.PortfolioId &&
                    assignments.Any(assignment =>
                        assignment.PortfolioId == property.PortfolioId &&
                        (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                         (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                          assignment.SelectedProperties.Any(scope =>
                              scope.PropertyId == property.Id &&
                              scope.PortfolioId == property.PortfolioId))))),
            cancellationToken);
    }

    private static Task<bool> DenyAssignedWorkUntilResponsibilityExists() =>
        // Every current WorkOrder-target capability is maintenance.assigned-work.*. No role scope,
        // including bad AllProperties/SelectedProperties data, may substitute for the missing
        // same-assignment responsibility join. This branch stays closed until that model lands.
        Task.FromResult(false);

    private IQueryable<MembershipRoleAssignment> EffectiveAssignments(
        ActiveAccessContext accessContext,
        string capabilityKey,
        CapabilityAuthorizationTargetKind targetKind,
        DateTime utcNow)
    {
        var effectiveContexts = _db.WorkspaceAccessContexts.AsNoTracking().WhereEffective();
        var effectiveMemberships = _db.WorkspaceMemberships.AsNoTracking().WhereEffective(utcNow);

        return _db.MembershipRoleAssignments
            .AsNoTracking()
            .WhereEffective(utcNow)
            .Where(assignment =>
                assignment.WorkspaceMembershipId == accessContext.WorkspaceMembershipId &&
                assignment.PortfolioId == accessContext.PortfolioId &&
                effectiveMemberships.Any(membership =>
                    membership.Id == assignment.WorkspaceMembershipId &&
                    membership.AccessContextId == accessContext.AccessContextId &&
                    membership.PortfolioId == assignment.PortfolioId) &&
                effectiveContexts.Any(context =>
                    context.Id == accessContext.AccessContextId &&
                    context.UserId == accessContext.UserId &&
                    context.PortfolioId == assignment.PortfolioId &&
                    context.AccessRevision == accessContext.AccessRevision) &&
                assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey &&
                    profileCapability.CapabilityDefinition.AuthorizationTargetKind == targetKind));
    }
}
