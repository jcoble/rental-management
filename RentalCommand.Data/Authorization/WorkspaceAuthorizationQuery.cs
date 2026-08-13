using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Reusable authorization query primitives for property records. Authorization stays in the
/// translated database query; hot reads can join the materialized relational set without ever
/// materializing an allowed-ID collection in application memory.
/// </summary>
public static class WorkspaceAuthorizationQuery
{
    public const string SecurityTimeAuthorizedPropertyIdsFunctionName =
        "public.rc_api_authorized_property_ids_at_security_time";

    /// <summary>
    /// Produces the caller's effective property ids as a composable, materialized PostgreSQL set.
    /// Hot report/accounting queries join this relation directly instead of embedding the effective
    /// capability function in a correlated <c>Any</c>/<c>EXISTS</c> for every output row. The result is
    /// never materialized by the application; EF composes the CTE into the statement that consumes it.
    /// </summary>
    public static IQueryable<int> AuthorizedPropertyIds(
        this RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys)
    {
        var keys = RequireCapabilityKeys(capabilityKeys);
        var targetKind = CapabilityAuthorizationTargetKind.Property.ToString();
        return db.Database.SqlQuery<int>($"""
            WITH effective_scopes AS MATERIALIZED (
                SELECT effective_scope."ScopeKind", effective_scope."PropertyId"
                FROM public.rc_api_effective_capability_scopes(
                    {scope.PortfolioId},
                    {scope.SessionId},
                    {scope.UserId},
                    {scope.AccessContextId},
                    {scope.AccessRevision},
                    {keys},
                    {targetKind}) AS effective_scope
            ),
            authorized_properties AS MATERIALIZED (
                SELECT DISTINCT property."Id" AS "PropertyId"
                FROM "Properties" AS property
                CROSS JOIN effective_scopes AS effective_scope
                WHERE property."PortfolioId" = {scope.PortfolioId}
                  AND property."DeletedAt" IS NULL
                  AND (effective_scope."ScopeKind" = 'AllProperties'
                       OR (effective_scope."ScopeKind" = 'SelectedProperties'
                           AND effective_scope."PropertyId" = property."Id"))
            )
            SELECT authorized_properties."PropertyId" AS "Value"
            FROM authorized_properties
            """);
    }

    /// <summary>
    /// Produces effective property ids using the caller-supplied security timestamp. This is the
    /// canonical database-side rendering for raw-SQL aggregates that must not use simulation time.
    /// Its function definition is kept beside, and mirrors, the LINQ policy in
    /// <see cref="AuthorizedAssignmentsForScope"/>.
    /// </summary>
    public static IQueryable<int> AuthorizedPropertyIds(
        this RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime securityNowUtc)
    {
        var keys = RequireCapabilityKeys(capabilityKeys);
        return db.Database.SqlQuery<int>($"""
            SELECT authorized_property."Value"
            FROM public.rc_api_authorized_property_ids_at_security_time(
                {scope.PortfolioId},
                {scope.SessionId},
                {scope.UserId},
                {scope.AccessContextId},
                {scope.AccessRevision},
                {keys},
                {securityNowUtc}) AS authorized_property
            """);
    }

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

    /// <summary>
    /// Applies the explicit session, access-context, membership, assignment, and capability
    /// predicates used by write handlers. Unlike the PostgreSQL request-scope function used by
    /// read endpoints, this query remains valid inside atomic command connections while still
    /// translating as part of the consuming SQL statement.
    /// </summary>
    public static IQueryable<MembershipRoleAssignment> AuthorizedAssignmentsForScope(
        this RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        CapabilityAuthorizationTargetKind targetKind,
        DateTime utcNow)
    {
        var keys = RequireCapabilityKeys(capabilityKeys);
        return db.MembershipRoleAssignments.Where(assignment =>
            assignment.PortfolioId == scope.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null
            && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= utcNow
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow)
            && assignment.WorkspaceMembership!.AccessContextId == scope.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == scope.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= utcNow
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > utcNow)
            && db.WorkspaceAccessContexts.Any(context =>
                context.Id == scope.AccessContextId
                && context.UserId == scope.UserId
                && context.PortfolioId == scope.PortfolioId
                && context.AccessRevision == scope.AccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            && db.AuthSessions.Any(session =>
                session.Id == scope.SessionId
                && session.UserId == scope.UserId
                && session.ActiveAccessContextId == scope.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > utcNow)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                keys.Contains(grant.CapabilityDefinition!.Key)
                && grant.CapabilityDefinition.AuthorizationTargetKind == targetKind));
    }

    /// <summary>
    /// Applies property scope to the explicit assignment query without materializing property ids.
    /// </summary>
    public static IQueryable<Property> WhereAuthorizedForScope(
        this IQueryable<Property> properties,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime utcNow)
    {
        var assignments = db.AuthorizedAssignmentsForScope(
            scope,
            capabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);
        return properties.Where(property =>
            property.PortfolioId == scope.PortfolioId
            && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                && assignment.SelectedProperties.Any(selected =>
                    selected.PortfolioId == scope.PortfolioId
                    && selected.PropertyId == property.Id)));
    }

    public static IQueryable<Property> WhereAuthorizedForScope(
        this IQueryable<Property> properties,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string capabilityKey,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);
        return properties.WhereAuthorizedForScope(db, scope, [capabilityKey], utcNow);
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
        _ = utcNow;
        var scopes = db.EffectiveCapabilityScopes(
            scope,
            RequireCapabilityKeys(capabilityKeys),
            targetKind);

        return db.MembershipRoleAssignments.AsNoTracking()
            .Where(assignment =>
                assignment.PortfolioId == scope.PortfolioId &&
                scopes.Any(effectiveScope =>
                    effectiveScope.AssignmentId == assignment.Id &&
                    effectiveScope.WorkspaceMembershipId == assignment.WorkspaceMembershipId &&
                    effectiveScope.ScopeKind == EffectiveCapabilityScopeQuery.AllPropertiesScope));
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
        return properties.WhereAuthorized(db, scope, [capabilityKey], utcNow);
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
        _ = utcNow;
        var scopes = db.EffectiveCapabilityScopes(
            scope,
            RequireCapabilityKeys(capabilityKeys),
            CapabilityAuthorizationTargetKind.Property);

        return properties.Where(property =>
            property.PortfolioId == scope.PortfolioId &&
            scopes.Any(effectiveScope =>
                effectiveScope.ScopeKind == EffectiveCapabilityScopeQuery.AllPropertiesScope ||
                effectiveScope.ScopeKind == EffectiveCapabilityScopeQuery.SelectedPropertiesScope &&
                effectiveScope.PropertyId == property.Id));
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
            (allPropertiesAssignments.Any(assignment =>
                 assignment.PortfolioId == application.PortfolioId) ||
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
        var keys = RequireCapabilityKeys(capabilityKeys);
        var propertyScopes = db.EffectiveCapabilityScopes(
            scope,
            keys,
            CapabilityAuthorizationTargetKind.Property);
        var assignedWorkScopes = db.EffectiveCapabilityScopes(
            scope,
            keys,
            CapabilityAuthorizationTargetKind.WorkOrder);

        return workOrders.Where(workOrder =>
            workOrder.PortfolioId == scope.PortfolioId &&
            (propertyScopes.Any(effectiveScope =>
                 effectiveScope.ScopeKind == EffectiveCapabilityScopeQuery.AllPropertiesScope ||
                 effectiveScope.ScopeKind == EffectiveCapabilityScopeQuery.SelectedPropertiesScope &&
                 effectiveScope.PropertyId == workOrder.PropertyId) ||
             assignedWorkScopes.Any(effectiveScope =>
                 effectiveScope.ScopeKind == EffectiveCapabilityScopeQuery.AssignedWorkOrdersScope &&
                 workOrder.Responsibilities.Any(responsibility =>
                     responsibility.PortfolioId == workOrder.PortfolioId &&
                     responsibility.WorkspaceMembershipId == effectiveScope.WorkspaceMembershipId &&
                     responsibility.MembershipRoleAssignmentId == effectiveScope.AssignmentId &&
                     responsibility.EffectiveFromUtc <= utcNow &&
                     (responsibility.EffectiveToUtc == null ||
                      responsibility.EffectiveToUtc > utcNow)))));
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
            ((conversation.PropertyId == null &&
              allPropertiesAssignments.Any(assignment =>
                  assignment.PortfolioId == conversation.PortfolioId)) ||
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
        var allPropertiesAssignments = db.AuthorizedAllPropertyAssignments(
            scope,
            keys,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);
        var authorizedProperties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, keys, utcNow);

        // A selected-property assignment sees a tenant only through a canonical lease-management
        // relationship in that property. Unattached tenants have no property scope and therefore
        // require an AllProperties assignment carrying one of the endpoint capabilities.
        return tenants.Where(tenant =>
            tenant.PortfolioId == scope.PortfolioId &&
            (allPropertiesAssignments.Any(assignment =>
                 assignment.PortfolioId == tenant.PortfolioId)
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
        _ = utcNow;
        var scopes = db.EffectiveCapabilityScopes(
            accessContext,
            [capabilityKey],
            CapabilityAuthorizationTargetKind.Property);

        return properties.Where(property =>
            property.PortfolioId == accessContext.PortfolioId &&
            scopes.Any(effectiveScope =>
                effectiveScope.ScopeKind == EffectiveCapabilityScopeQuery.AllPropertiesScope ||
                effectiveScope.ScopeKind == EffectiveCapabilityScopeQuery.SelectedPropertiesScope &&
                effectiveScope.PropertyId == property.Id));
    }
}
