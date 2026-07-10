using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Validates stored role profile, stored assignment scope kind, and stored child cardinality in one
/// translated SQL projection. Mutation commands call this after their final flush and before
/// transaction commit. The final baseline may additionally enforce the invariant with a deferred
/// constraint trigger.
/// </summary>
public sealed class MembershipAssignmentScopeValidator : IMembershipAssignmentScopeValidator
{
    private readonly RentalCommandDbContext _db;

    public MembershipAssignmentScopeValidator(RentalCommandDbContext db) => _db = db;

    public async Task ValidateAsync(
        IReadOnlyCollection<int> assignmentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignmentIds);
        if (assignmentIds.Count == 0)
        {
            return;
        }

        var invalid = await _db.MembershipRoleAssignments
            .AsNoTracking()
            .Where(assignment => assignmentIds.Contains(assignment.Id))
            .Select(assignment => new
            {
                assignment.Id,
                RoleProfileKey = assignment.RoleProfile!.Key,
                assignment.ScopeKind,
                SelectedPropertyCount = assignment.SelectedProperties.Count(),
            })
            .Where(scope =>
                (scope.RoleProfileKey == RoleProfileKeys.WorkspaceAdministrator &&
                 scope.ScopeKind != MembershipRoleAssignmentScopeKind.AllProperties) ||
                ((scope.RoleProfileKey == RoleProfileKeys.PropertyManager ||
                  scope.RoleProfileKey == RoleProfileKeys.LeasingAgent) &&
                 scope.ScopeKind != MembershipRoleAssignmentScopeKind.AllProperties &&
                 scope.ScopeKind != MembershipRoleAssignmentScopeKind.SelectedProperties) ||
                (scope.RoleProfileKey == RoleProfileKeys.MaintenanceTechnician &&
                 scope.ScopeKind != MembershipRoleAssignmentScopeKind.AssignedWorkOrders) ||
                (scope.RoleProfileKey != RoleProfileKeys.WorkspaceAdministrator &&
                 scope.RoleProfileKey != RoleProfileKeys.PropertyManager &&
                 scope.RoleProfileKey != RoleProfileKeys.LeasingAgent &&
                 scope.RoleProfileKey != RoleProfileKeys.MaintenanceTechnician) ||
                (scope.ScopeKind != MembershipRoleAssignmentScopeKind.AllProperties &&
                 scope.ScopeKind != MembershipRoleAssignmentScopeKind.SelectedProperties &&
                 scope.ScopeKind != MembershipRoleAssignmentScopeKind.AssignedWorkOrders) ||
                (scope.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                 scope.SelectedPropertyCount == 0) ||
                (scope.ScopeKind != MembershipRoleAssignmentScopeKind.SelectedProperties &&
                 scope.SelectedPropertyCount != 0))
            .OrderBy(scope => scope.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (invalid is null)
        {
            return;
        }

        throw new DomainValidationException(
            !RoleAllowsScope(invalid.RoleProfileKey, invalid.ScopeKind)
                ? $"Role {invalid.RoleProfileKey} cannot use {invalid.ScopeKind} assignment scope."
                : invalid.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                ? $"Assignment {invalid.Id} must contain at least one selected property."
                : $"Assignment {invalid.Id} cannot contain selected-property rows for {invalid.ScopeKind} scope.");
    }

    private static bool RoleAllowsScope(
        string roleProfileKey,
        MembershipRoleAssignmentScopeKind scopeKind) =>
        roleProfileKey switch
        {
            RoleProfileKeys.WorkspaceAdministrator =>
                scopeKind == MembershipRoleAssignmentScopeKind.AllProperties,
            RoleProfileKeys.PropertyManager or RoleProfileKeys.LeasingAgent =>
                scopeKind is MembershipRoleAssignmentScopeKind.AllProperties or
                    MembershipRoleAssignmentScopeKind.SelectedProperties,
            RoleProfileKeys.MaintenanceTechnician =>
                scopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders,
            _ => false,
        };
}
