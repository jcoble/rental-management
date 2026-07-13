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
        return HasAnyCapabilityAsync(
            accessContext,
            new[] { capabilityKey },
            target,
            utcNow,
            cancellationToken);
    }

    public Task<bool> HasAnyCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        WorkspaceAuthorizationTarget? target,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityKeys);
        var requestedCapabilityKeys = capabilityKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requestedCapabilityKeys.Length == 0)
        {
            return Task.FromResult(false);
        }

        if (target is null || target.PortfolioId != accessContext.PortfolioId)
        {
            return Task.FromResult(false);
        }

        return target switch
        {
            WorkspaceCapabilityAuthorizationTarget workspace =>
                HasWorkspaceCapabilityAsync(accessContext, requestedCapabilityKeys, workspace, utcNow, cancellationToken),
            PropertyCapabilityAuthorizationTarget property =>
                HasPropertyCapabilityAsync(accessContext, requestedCapabilityKeys, property, utcNow, cancellationToken),
            PortfolioWidePropertyCapabilityAuthorizationTarget portfolioWide =>
                HasPortfolioWidePropertyCapabilityAsync(
                    accessContext, requestedCapabilityKeys, portfolioWide, utcNow, cancellationToken),
            UnitCapabilityAuthorizationTarget unit =>
                HasUnitCapabilityAsync(accessContext, requestedCapabilityKeys, unit, utcNow, cancellationToken),
            RentalApplicationCapabilityAuthorizationTarget application =>
                HasApplicationCapabilityAsync(accessContext, requestedCapabilityKeys, application, utcNow, cancellationToken),
            TenantCapabilityAuthorizationTarget tenant =>
                HasTenantCapabilityAsync(accessContext, requestedCapabilityKeys, tenant, utcNow, cancellationToken),
            LeaseManagementCapabilityAuthorizationTarget management =>
                HasLeaseManagementCapabilityAsync(
                    accessContext, requestedCapabilityKeys, management, utcNow, cancellationToken),
            LeaseAgreementCapabilityAuthorizationTarget agreement =>
                HasLeaseAgreementCapabilityAsync(
                    accessContext, requestedCapabilityKeys, agreement, utcNow, cancellationToken),
            LeaseAddendumCapabilityAuthorizationTarget addendum =>
                HasLeaseAddendumCapabilityAsync(
                    accessContext, requestedCapabilityKeys, addendum, utcNow, cancellationToken),
            WorkOrderCapabilityAuthorizationTarget workOrder =>
                HasWorkOrderCapabilityAsync(accessContext, requestedCapabilityKeys, workOrder, utcNow, cancellationToken),
            _ => Task.FromResult(false),
        };
    }

    private Task<bool> HasWorkspaceCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        WorkspaceCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken) =>
        EffectiveAssignments(
                accessContext,
                capabilityKeys,
                CapabilityAuthorizationTargetKind.Workspace,
                utcNow)
            .AnyAsync(assignment =>
                assignment.PortfolioId == target.PortfolioId &&
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties,
                cancellationToken);

    private Task<bool> HasPropertyCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        PropertyCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var assignments = EffectiveAssignments(
            accessContext,
            capabilityKeys,
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

    private Task<bool> HasPortfolioWidePropertyCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        PortfolioWidePropertyCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken) =>
        EffectiveAssignments(
                accessContext,
                capabilityKeys,
                CapabilityAuthorizationTargetKind.Property,
                utcNow)
            .AnyAsync(assignment =>
                assignment.PortfolioId == target.PortfolioId &&
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties,
                cancellationToken);

    private Task<bool> HasUnitCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        UnitCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var assignments = EffectiveAssignments(
            accessContext,
            capabilityKeys,
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
        IReadOnlyCollection<string> capabilityKeys,
        RentalApplicationCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var assignments = EffectiveAssignments(
            accessContext,
            capabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);

        return _db.RentalApplications.AsNoTracking().AnyAsync(application =>
                application.Id == target.ApplicationId &&
                application.PortfolioId == target.PortfolioId &&
                application.PortfolioId == accessContext.PortfolioId &&
                assignments.Any(assignment =>
                    assignment.PortfolioId == application.PortfolioId &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (application.PropertyId != null &&
                      assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      assignment.SelectedProperties.Any(scope =>
                          scope.PropertyId == application.PropertyId.Value &&
                          scope.PortfolioId == application.PortfolioId)))),
            cancellationToken);
    }

    private Task<bool> HasTenantCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        TenantCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var assignments = EffectiveAssignments(
            accessContext,
            capabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);

        return _db.Tenants.AsNoTracking().AnyAsync(tenant =>
                tenant.Id == target.TenantId &&
                tenant.PortfolioId == target.PortfolioId &&
                tenant.PortfolioId == accessContext.PortfolioId &&
                assignments.Any(assignment =>
                    assignment.PortfolioId == tenant.PortfolioId &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      tenant.LeaseManagementParties.Any(party =>
                          party.PortfolioId == tenant.PortfolioId &&
                          party.LeaseManagement != null &&
                          assignment.SelectedProperties.Any(scope =>
                              scope.PortfolioId == tenant.PortfolioId &&
                              scope.PropertyId == party.LeaseManagement.PropertyId))))),
            cancellationToken);
    }

    private Task<bool> HasWorkOrderCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        WorkOrderCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        // Management work.read/work.manage are property-target capabilities. Assigned-work
        // capabilities have target kind WorkOrder and therefore cannot enter this assignment query;
        // they remain fail-closed until WorkOrderResponsibility exists.
        var assignments = EffectiveAssignments(
            accessContext,
            capabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);

        return _db.WorkOrders.AsNoTracking().AnyAsync(workOrder =>
                workOrder.Id == target.WorkOrderId &&
                workOrder.PortfolioId == target.PortfolioId &&
                workOrder.PortfolioId == accessContext.PortfolioId &&
                assignments.Any(assignment =>
                    assignment.PortfolioId == workOrder.PortfolioId &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      assignment.SelectedProperties.Any(scope =>
                          scope.PortfolioId == workOrder.PortfolioId &&
                          scope.PropertyId == workOrder.PropertyId)))),
            cancellationToken);
    }

    private Task<bool> HasLeaseManagementCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        LeaseManagementCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var scope = ToReadScope(accessContext);
        return _db.LeaseManagements.AsNoTracking()
            .WhereAuthorized(_db, scope, capabilityKeys, utcNow)
            .AnyAsync(management => management.Id == target.LeaseManagementId
                && management.PortfolioId == target.PortfolioId, cancellationToken);
    }

    private Task<bool> HasLeaseAgreementCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        LeaseAgreementCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var authorizedManagements = _db.LeaseManagements.AsNoTracking()
            .WhereAuthorized(_db, ToReadScope(accessContext), capabilityKeys, utcNow);
        return _db.LeaseAgreements.AsNoTracking().AnyAsync(agreement =>
            agreement.Id == target.LeaseAgreementId &&
            agreement.PortfolioId == target.PortfolioId &&
            authorizedManagements.Any(management =>
                management.Id == agreement.LeaseManagementId &&
                management.PortfolioId == agreement.PortfolioId), cancellationToken);
    }

    private Task<bool> HasLeaseAddendumCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        LeaseAddendumCapabilityAuthorizationTarget target,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var authorizedManagements = _db.LeaseManagements.AsNoTracking()
            .WhereAuthorized(_db, ToReadScope(accessContext), capabilityKeys, utcNow);
        return _db.LeaseAddenda.AsNoTracking().AnyAsync(addendum =>
            addendum.Id == target.LeaseAddendumId &&
            addendum.PortfolioId == target.PortfolioId &&
            authorizedManagements.Any(management =>
                management.Id == addendum.LeaseManagementId &&
                management.PortfolioId == addendum.PortfolioId), cancellationToken);
    }

    private static WorkspaceReadScope ToReadScope(ActiveAccessContext accessContext) => new(
        accessContext.PortfolioId,
        accessContext.UserId,
        accessContext.SessionId,
        accessContext.AccessContextId,
        accessContext.AccessRevision);

    private IQueryable<MembershipRoleAssignment> EffectiveAssignments(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
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
                    capabilityKeys.Contains(profileCapability.CapabilityDefinition!.Key) &&
                    profileCapability.CapabilityDefinition.AuthorizationTargetKind == targetKind));
    }
}
