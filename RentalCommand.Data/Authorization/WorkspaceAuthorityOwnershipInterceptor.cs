using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Rejects reparenting of durable authorization rows. Moving a child to another root could let the
/// caller advance one access revision while silently changing another root's authority. A transfer
/// must revoke the old fact and create a new fact beneath the destination root.
/// </summary>
public sealed class WorkspaceAuthorityOwnershipInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Validate(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Validate(eventData.Context);
        return ValueTask.FromResult(result);
    }

    internal static void Validate(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        RejectChanged<WorkspaceAccessContext>(context, nameof(WorkspaceAccessContext.UserId));
        RejectChanged<WorkspaceAccessContext>(context, nameof(WorkspaceAccessContext.PortfolioId));
        RejectChanged<WorkspaceMembership>(context, nameof(WorkspaceMembership.AccessContextId));
        RejectChanged<WorkspaceMembership>(context, nameof(WorkspaceMembership.PortfolioId));
        RejectChanged<MembershipRoleAssignment>(context, nameof(MembershipRoleAssignment.WorkspaceMembershipId));
        RejectChanged<MembershipRoleAssignment>(context, nameof(MembershipRoleAssignment.PortfolioId));
        RejectChanged<MembershipRoleAssignment>(context, nameof(MembershipRoleAssignment.RoleProfileId));
        RejectChanged<MembershipRoleAssignmentProperty>(
            context, nameof(MembershipRoleAssignmentProperty.MembershipRoleAssignmentId));
        RejectChanged<MembershipRoleAssignmentProperty>(context, nameof(MembershipRoleAssignmentProperty.PortfolioId));
    }

    private static void RejectChanged<TEntity>(DbContext context, string propertyName)
        where TEntity : class
    {
        foreach (var entry in context.ChangeTracker.Entries<TEntity>()
                     .Where(item => item.State == EntityState.Modified))
        {
            var property = entry.Property(propertyName);
            if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
            {
                throw new AccessAuthorityMutationException(
                    $"{typeof(TEntity).Name}.{propertyName} is immutable after creation.");
            }
        }
    }
}
