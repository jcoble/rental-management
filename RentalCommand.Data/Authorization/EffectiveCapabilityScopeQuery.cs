using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// One relational authorization-scope row produced by PostgreSQL. All-properties and assigned-work
/// assignments have a null PropertyId; selected-property assignments produce one row per selected
/// property. The row set stays composable and must never be materialized as an allowed-ID list.
/// </summary>
internal sealed class EffectiveCapabilityScopeRow
{
    public int AssignmentId { get; init; }
    public int WorkspaceMembershipId { get; init; }
    public string ScopeKind { get; init; } = string.Empty;
    public int? PropertyId { get; init; }
}

internal static class EffectiveCapabilityScopeQuery
{
    internal const string AllPropertiesScope = nameof(MembershipRoleAssignmentScopeKind.AllProperties);
    internal const string SelectedPropertiesScope = nameof(MembershipRoleAssignmentScopeKind.SelectedProperties);
    internal const string AssignedWorkOrdersScope = nameof(MembershipRoleAssignmentScopeKind.AssignedWorkOrders);

    internal static IQueryable<EffectiveCapabilityScopeRow> EffectiveCapabilityScopes(
        this RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        CapabilityAuthorizationTargetKind targetKind)
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

        var targetKindName = targetKind.ToString();
        return db.Database.SqlQuery<EffectiveCapabilityScopeRow>($"""
            SELECT effective_scope."AssignmentId",
                   effective_scope."WorkspaceMembershipId",
                   effective_scope."ScopeKind",
                   effective_scope."PropertyId"
            FROM public.rc_api_effective_capability_scopes(
                {scope.PortfolioId},
                {scope.SessionId},
                {scope.UserId},
                {scope.AccessContextId},
                {scope.AccessRevision},
                {keys},
                {targetKindName}) AS effective_scope
            """);
    }

    internal static IQueryable<EffectiveCapabilityScopeRow> EffectiveCapabilityScopes(
        this RentalCommandDbContext db,
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        CapabilityAuthorizationTargetKind targetKind) =>
        db.EffectiveCapabilityScopes(
            new WorkspaceReadScope(
                accessContext.PortfolioId,
                accessContext.UserId,
                accessContext.SessionId,
                accessContext.AccessContextId,
                accessContext.AccessRevision),
            capabilityKeys,
            targetKind);
}
