using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Reusable authorization query primitive for property records. The correlated EXISTS remains part
/// of the caller's IQueryable so authorization runs before projection, count, sort, and paging; no
/// allowed-ID collection is ever materialized.
/// </summary>
public static class WorkspaceAuthorizationQuery
{
    private static string[] RequireCapabilityKeys(IReadOnlyCollection<string> capabilityKeys)
    {
        ArgumentNullException.ThrowIfNull(capabilityKeys);
        var keys = capabilityKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
        {
            throw new ArgumentException("At least one capability key is required.", nameof(capabilityKeys));
        }

        return keys;
    }

    public static IQueryable<MembershipRoleAssignment> AuthorizedAllPropertyAssignments(
        this RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string capabilityKey,
        CapabilityAuthorizationTargetKind targetKind,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);
        return db.AuthorizedWorkspaceAssignments(scope, [capabilityKey], targetKind, utcNow);
    }

    public static IQueryable<MembershipRoleAssignment> AuthorizedAllPropertyAssignments(
        this RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        CapabilityAuthorizationTargetKind targetKind,
        DateTime utcNow)
        => db.AuthorizedWorkspaceAssignments(scope, capabilityKeys, targetKind, utcNow);

    /// <summary>
    /// Proves that one current, effective AllProperties assignment carries any requested capability
    /// with the supplied target kind. This is the fail-closed primitive for property-target actions
    /// that do not yet have one exact Property row, such as creating a Property or an unattached
    /// OwnerEntity. It does not reinterpret a property capability as workspace-target authority.
    /// </summary>
    public static IQueryable<MembershipRoleAssignment> AuthorizedWorkspaceAssignments(
        this RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        CapabilityAuthorizationTargetKind targetKind,
        DateTime utcNow)
    {
        var keys = RequireCapabilityKeys(capabilityKeys);

        return db.MembershipRoleAssignments.AsNoTracking()
            .Where(assignment =>
                assignment.PortfolioId == scope.PortfolioId &&
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.SuspendedAtUtc == null &&
                assignment.RevokedAtUtc == null &&
                assignment.EffectiveFromUtc <= utcNow &&
                (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                assignment.WorkspaceMembership!.AccessContextId == scope.AccessContextId &&
                assignment.WorkspaceMembership.PortfolioId == scope.PortfolioId &&
                assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
                assignment.WorkspaceMembership.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.RevokedAtUtc == null &&
                assignment.WorkspaceMembership.EffectiveFromUtc <= utcNow &&
                (assignment.WorkspaceMembership.EffectiveToUtc == null ||
                 assignment.WorkspaceMembership.EffectiveToUtc > utcNow) &&
                assignment.WorkspaceMembership.AccessContext!.UserId == scope.UserId &&
                assignment.WorkspaceMembership.AccessContext.AccessRevision == scope.AccessRevision &&
                assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
                assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null &&
                db.AuthSessions.AsNoTracking().Any(session =>
                    session.Id == scope.SessionId &&
                    session.UserId == scope.UserId &&
                    session.ActiveAccessContextId == scope.AccessContextId &&
                    session.Status == AuthSessionStatus.Active &&
                    session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > utcNow) &&
                assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    keys.Contains(profileCapability.CapabilityDefinition!.Key) &&
                    profileCapability.CapabilityDefinition.AuthorizationTargetKind == targetKind));
    }

    /// <summary>
    /// Applies the current-session, access-revision, capability, and selected-property predicates
    /// as a correlated EXISTS inside the caller's property query. This is the list/report primitive;
    /// it does not materialize allowed property ids.
    /// </summary>
    public static IQueryable<Property> WhereAuthorized(
        this IQueryable<Property> properties,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string capabilityKey,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        return properties.Where(property =>
            property.PortfolioId == scope.PortfolioId &&
            db.AuthSessions.AsNoTracking().Any(session =>
                session.Id == scope.SessionId &&
                session.UserId == scope.UserId &&
                session.ActiveAccessContextId == scope.AccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > utcNow &&
                session.ActiveAccessContext != null &&
                session.ActiveAccessContext.Id == scope.AccessContextId &&
                session.ActiveAccessContext.UserId == scope.UserId &&
                session.ActiveAccessContext.PortfolioId == scope.PortfolioId &&
                session.ActiveAccessContext.AccessRevision == scope.AccessRevision &&
                session.ActiveAccessContext.Status == WorkspaceAccessContextStatus.Active &&
                session.ActiveAccessContext.SuspendedAtUtc == null &&
                session.ActiveAccessContext.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership != null &&
                session.ActiveAccessContext.Membership.PortfolioId == property.PortfolioId &&
                session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active &&
                session.ActiveAccessContext.Membership.SuspendedAtUtc == null &&
                session.ActiveAccessContext.Membership.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow &&
                (session.ActiveAccessContext.Membership.EffectiveToUtc == null ||
                 session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow) &&
                session.ActiveAccessContext.Membership.RoleAssignments.Any(assignment =>
                    assignment.PortfolioId == property.PortfolioId &&
                    assignment.Status == MembershipRoleAssignmentStatus.Active &&
                    assignment.SuspendedAtUtc == null &&
                    assignment.RevokedAtUtc == null &&
                    assignment.EffectiveFromUtc <= utcNow &&
                    (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                    assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                        profileCapability.CapabilityDefinition!.Key == capabilityKey &&
                        profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                            CapabilityAuthorizationTargetKind.Property) &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      assignment.SelectedProperties.Any(selected =>
                          selected.PortfolioId == property.PortfolioId &&
                          selected.PropertyId == property.Id))))));
    }

    /// <summary>
    /// Applies any one of the endpoint-owned property capabilities while keeping the capability and
    /// selected-property predicates on the same assignment row. The array becomes one SQL IN/ANY
    /// predicate; it is never evaluated after materialization.
    /// </summary>
    public static IQueryable<Property> WhereAuthorized(
        this IQueryable<Property> properties,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime utcNow)
    {
        var keys = RequireCapabilityKeys(capabilityKeys);

        return properties.Where(property =>
            property.PortfolioId == scope.PortfolioId &&
            db.AuthSessions.AsNoTracking().Any(session =>
                session.Id == scope.SessionId &&
                session.UserId == scope.UserId &&
                session.ActiveAccessContextId == scope.AccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > utcNow &&
                session.ActiveAccessContext != null &&
                session.ActiveAccessContext.Id == scope.AccessContextId &&
                session.ActiveAccessContext.UserId == scope.UserId &&
                session.ActiveAccessContext.PortfolioId == scope.PortfolioId &&
                session.ActiveAccessContext.AccessRevision == scope.AccessRevision &&
                session.ActiveAccessContext.Status == WorkspaceAccessContextStatus.Active &&
                session.ActiveAccessContext.SuspendedAtUtc == null &&
                session.ActiveAccessContext.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership != null &&
                session.ActiveAccessContext.Membership.PortfolioId == property.PortfolioId &&
                session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active &&
                session.ActiveAccessContext.Membership.SuspendedAtUtc == null &&
                session.ActiveAccessContext.Membership.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow &&
                (session.ActiveAccessContext.Membership.EffectiveToUtc == null ||
                 session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow) &&
                session.ActiveAccessContext.Membership.RoleAssignments.Any(assignment =>
                    assignment.PortfolioId == property.PortfolioId &&
                    assignment.Status == MembershipRoleAssignmentStatus.Active &&
                    assignment.SuspendedAtUtc == null &&
                    assignment.RevokedAtUtc == null &&
                    assignment.EffectiveFromUtc <= utcNow &&
                    (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                    assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                        keys.Contains(profileCapability.CapabilityDefinition!.Key) &&
                        profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                            CapabilityAuthorizationTargetKind.Property) &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      assignment.SelectedProperties.Any(selected =>
                          selected.PortfolioId == property.PortfolioId &&
                          selected.PropertyId == property.Id))))));
    }

    public static IQueryable<RentalApplication> WhereAuthorized(
        this IQueryable<RentalApplication> applications,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime utcNow)
    {
        var allPropertiesAssignments = db.AuthorizedAllPropertyAssignments(
            scope,
            capabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);
        var authorizedProperties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, capabilityKeys, utcNow);

        return applications.Where(application =>
            application.PortfolioId == scope.PortfolioId &&
            (allPropertiesAssignments.Any() ||
             (application.PropertyId != null &&
              authorizedProperties.Any(property =>
                  property.Id == application.PropertyId &&
                  property.PortfolioId == application.PortfolioId))));
    }

    public static IQueryable<WorkOrder> WhereAuthorized(
        this IQueryable<WorkOrder> workOrders,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime utcNow)
    {
        var authorizedProperties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, capabilityKeys, utcNow);

        return workOrders.Where(workOrder =>
            workOrder.PortfolioId == scope.PortfolioId &&
            authorizedProperties.Any(property =>
                property.Id == workOrder.PropertyId &&
                property.PortfolioId == workOrder.PortfolioId));
    }

    public static IQueryable<Conversation> WhereAuthorized(
        this IQueryable<Conversation> conversations,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime utcNow)
    {
        var allPropertiesAssignments = db.AuthorizedAllPropertyAssignments(
            scope,
            capabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);
        var authorizedProperties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, capabilityKeys, utcNow);

        // Every new property-scoped thread stores its canonical PropertyId. A null property is a
        // genuinely workspace-wide/unattached thread and is visible only to an AllProperties
        // assignment; selected-property access never falls back through portfolio or tenant alone.
        return conversations.Where(conversation =>
            conversation.PortfolioId == scope.PortfolioId &&
            ((conversation.PropertyId == null && allPropertiesAssignments.Any()) ||
             (conversation.PropertyId != null &&
              authorizedProperties.Any(property =>
                  property.Id == conversation.PropertyId &&
                  property.PortfolioId == conversation.PortfolioId))));
    }

    public static IQueryable<Tenant> WhereAuthorized(
        this IQueryable<Tenant> tenants,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime utcNow)
    {
        var keys = RequireCapabilityKeys(capabilityKeys);
        var authorizedProperties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, keys, utcNow);

        // A selected-property assignment sees a tenant only through a canonical lease-management
        // relationship in that property. Unattached tenants have no property scope and therefore
        // require an AllProperties assignment carrying one of the endpoint capabilities.
        return tenants.Where(tenant =>
            tenant.PortfolioId == scope.PortfolioId &&
            (db.AuthorizedAllPropertyAssignments(
                    scope,
                    keys,
                    CapabilityAuthorizationTargetKind.Property,
                    utcNow).Any()
             || tenant.LeaseManagementParties.Any(party =>
                 party.PortfolioId == tenant.PortfolioId &&
                 party.LeaseManagement != null &&
                 authorizedProperties.Any(property =>
                     property.Id == party.LeaseManagement.PropertyId &&
                    property.PortfolioId == tenant.PortfolioId))));
    }

    /// <summary>
    /// Applies one current-session/capability/property-scope proof to canonical lease-management
    /// relationships. The allowed Property set remains a correlated SQL subquery.
    /// </summary>
    public static IQueryable<LeaseManagement> WhereAuthorized(
        this IQueryable<LeaseManagement> leaseManagements,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime utcNow)
    {
        var authorizedProperties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, capabilityKeys, utcNow);

        return leaseManagements.Where(management =>
            management.PortfolioId == scope.PortfolioId &&
            authorizedProperties.Any(property =>
                property.Id == management.PropertyId &&
                property.PortfolioId == management.PortfolioId));
    }

    public static IQueryable<Property> WhereAuthorized(
        this IQueryable<Property> properties,
        RentalCommandDbContext db,
        ActiveAccessContext accessContext,
        string capabilityKey,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        var effectiveContexts = db.WorkspaceAccessContexts.AsNoTracking().WhereEffective();
        var effectiveMemberships = db.WorkspaceMemberships.AsNoTracking().WhereEffective(utcNow);
        var effectiveAssignments = db.MembershipRoleAssignments.AsNoTracking().WhereEffective(utcNow);

        return properties.Where(property =>
            property.PortfolioId == accessContext.PortfolioId &&
            effectiveAssignments.Any(assignment =>
                assignment.WorkspaceMembershipId == accessContext.WorkspaceMembershipId &&
                assignment.PortfolioId == property.PortfolioId &&
                effectiveMemberships.Any(membership =>
                    membership.Id == assignment.WorkspaceMembershipId &&
                    membership.AccessContextId == accessContext.AccessContextId &&
                    membership.PortfolioId == property.PortfolioId) &&
                effectiveContexts.Any(context =>
                    context.Id == accessContext.AccessContextId &&
                    context.UserId == accessContext.UserId &&
                    context.PortfolioId == property.PortfolioId &&
                    context.AccessRevision == accessContext.AccessRevision) &&
                assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey &&
                    profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.Property) &&
                (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                 (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                  assignment.SelectedProperties.Any(scope =>
                      scope.PropertyId == property.Id &&
                      scope.PortfolioId == property.PortfolioId)))));
    }
}
