using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Makes a capability decision with one correlated database EXISTS. Capability and property scope
/// are intentionally predicates on the same assignment row, preventing multi-assignment leakage.
/// </summary>
public sealed class WorkspaceAuthorizationEvaluator : IWorkspaceAuthorizationEvaluator
{
    private readonly RentalCommandDbContext _db;

    public WorkspaceAuthorizationEvaluator(RentalCommandDbContext db) => _db = db;

    public Task<bool> HasCapabilityAsync(
        ActiveAccessContext accessContext,
        string capabilityKey,
        int? propertyId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        return _db.MembershipRoleAssignments
            .AsNoTracking()
            .AnyAsync(assignment =>
                assignment.WorkspaceMembershipId == accessContext.WorkspaceMembershipId &&
                assignment.PortfolioId == accessContext.PortfolioId &&
                assignment.WorkspaceMembership!.AccessContextId == accessContext.AccessContextId &&
                assignment.WorkspaceMembership.AccessContext!.UserId == accessContext.UserId &&
                assignment.WorkspaceMembership.AccessContext.AccessRevision == accessContext.AccessRevision &&
                assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
                assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
                assignment.WorkspaceMembership.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.RevokedAtUtc == null &&
                assignment.WorkspaceMembership.EffectiveFromUtc <= utcNow &&
                (assignment.WorkspaceMembership.EffectiveToUtc == null ||
                 assignment.WorkspaceMembership.EffectiveToUtc > utcNow) &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.SuspendedAtUtc == null &&
                assignment.RevokedAtUtc == null &&
                assignment.EffectiveFromUtc <= utcNow &&
                (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey) &&
                (propertyId == null ||
                 assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                 (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                  assignment.SelectedProperties.Any(scope => scope.PropertyId == propertyId))),
                cancellationToken);
    }
}
