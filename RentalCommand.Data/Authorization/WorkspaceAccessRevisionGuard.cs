using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Persistence guard for the opt-in access mutation boundary. It rejects authority changes unless
/// one tracked access root advances from the expected revision to exactly expected + 1, and proves
/// every changed child belongs to that root. It is intentionally not installed globally before
/// cutover, because legacy writes do not yet participate in the new access model.
/// </summary>
public sealed class WorkspaceAccessRevisionGuard
{
    public async Task<WorkspaceAccessGuardResult> ValidatePendingMutationAsync(
        RentalCommandDbContext db,
        int accessContextId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        WorkspaceAuthorityOwnershipInterceptor.Validate(db);

        var rootEntries = db.ChangeTracker.Entries<WorkspaceAccessContext>()
            .Where(entry => entry.Entity.Id == accessContextId)
            .ToArray();
        if (rootEntries.Length != 1)
        {
            throw new AccessAuthorityMutationException(
                "The access mutation must track exactly one matching workspace access root.");
        }

        var rootEntry = rootEntries[0];
        if (AuthorityEntries<WorkspaceAccessContext>(db)
            .Any(entry => entry.Entity.Id != accessContextId))
        {
            throw WrongRoot();
        }

        var revisionEntry = rootEntry.Property(context => context.AccessRevision);
        var originalRevision = revisionEntry.OriginalValue;
        var currentRevision = revisionEntry.CurrentValue;
        if (rootEntry.State != EntityState.Modified ||
            !revisionEntry.IsModified ||
            originalRevision != expectedRevision ||
            currentRevision != checked(expectedRevision + 1))
        {
            throw new AccessAuthorityMutationException(
                "Authority changes require the access revision to advance by exactly one from the expected revision.");
        }

        var hasRootAuthorityChange =
            rootEntry.Property(context => context.Status).IsModified ||
            rootEntry.Property(context => context.SuspendedAtUtc).IsModified ||
            rootEntry.Property(context => context.RevokedAtUtc).IsModified;

        var membershipIdsToResolve = new HashSet<int>();
        var assignmentIdsToResolve = new HashSet<int>();
        var assignmentsToValidate = new HashSet<MembershipRoleAssignment>();
        var assignmentIdsToValidate = new HashSet<int>();
        var hasChildAuthorityChange = false;

        foreach (var membershipEntry in AuthorityEntries<WorkspaceMembership>(db))
        {
            hasChildAuthorityChange = true;
            if (membershipEntry.Entity.AccessContextId != accessContextId)
            {
                throw WrongRoot();
            }

        }

        foreach (var assignmentEntry in AuthorityEntries<MembershipRoleAssignment>(db))
        {
            hasChildAuthorityChange = true;
            var assignment = assignmentEntry.Entity;
            ValidateAssignmentOwnerOrDefer(
                assignment, accessContextId, membershipIdsToResolve);

            if (assignmentEntry.State != EntityState.Deleted)
            {
                assignmentsToValidate.Add(assignment);
            }
        }

        foreach (var scopeEntry in AuthorityEntries<MembershipRoleAssignmentProperty>(db))
        {
            hasChildAuthorityChange = true;
            var scope = scopeEntry.Entity;
            var assignment = scope.MembershipRoleAssignment;
            if (assignment?.WorkspaceMembership is not null)
            {
                if (assignment.WorkspaceMembership.AccessContextId != accessContextId)
                {
                    throw WrongRoot();
                }

                if (db.Entry(assignment).State != EntityState.Deleted)
                {
                    assignmentsToValidate.Add(assignment);
                }
            }
            else if (scope.MembershipRoleAssignmentId > 0)
            {
                assignmentIdsToResolve.Add(scope.MembershipRoleAssignmentId);
                assignmentIdsToValidate.Add(scope.MembershipRoleAssignmentId);
            }
            else
            {
                throw new AccessAuthorityMutationException(
                    "A new property scope must be attached to a tracked role assignment.");
            }
        }

        if (!hasRootAuthorityChange && !hasChildAuthorityChange)
        {
            throw new AccessAuthorityMutationException(
                "The access revision cannot advance without a matching authority mutation.");
        }

        await ValidateDeferredOwnershipAsync(
            db,
            accessContextId,
            membershipIdsToResolve,
            assignmentIdsToResolve,
            cancellationToken);

        return new WorkspaceAccessGuardResult(assignmentsToValidate, assignmentIdsToValidate);
    }

    private static IEnumerable<EntityEntry<TEntity>> AuthorityEntries<TEntity>(RentalCommandDbContext db)
        where TEntity : class =>
        db.ChangeTracker.Entries<TEntity>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    private static void ValidateAssignmentOwnerOrDefer(
        MembershipRoleAssignment assignment,
        int accessContextId,
        ISet<int> membershipIdsToResolve)
    {
        if (assignment.WorkspaceMembership is not null)
        {
            if (assignment.WorkspaceMembership.AccessContextId != accessContextId)
            {
                throw WrongRoot();
            }

            return;
        }

        if (assignment.WorkspaceMembershipId <= 0)
        {
            throw new AccessAuthorityMutationException(
                "A new role assignment must be attached to a tracked workspace membership.");
        }

        membershipIdsToResolve.Add(assignment.WorkspaceMembershipId);
    }

    private static async Task ValidateDeferredOwnershipAsync(
        RentalCommandDbContext db,
        int accessContextId,
        IReadOnlyCollection<int> membershipIds,
        IReadOnlyCollection<int> assignmentIds,
        CancellationToken cancellationToken)
    {
        if (membershipIds.Count == 0 && assignmentIds.Count == 0)
        {
            return;
        }

        var owners = db.WorkspaceMemberships
            .AsNoTracking()
            .Where(membership => membershipIds.Contains(membership.Id))
            .Select(membership => new
            {
                ReferenceKind = 1,
                ReferenceId = membership.Id,
                membership.AccessContextId,
            })
            .Concat(db.MembershipRoleAssignments
                .AsNoTracking()
                .Where(assignment => assignmentIds.Contains(assignment.Id))
                .Select(assignment => new
                {
                    ReferenceKind = 2,
                    ReferenceId = assignment.Id,
                    AccessContextId = assignment.WorkspaceMembership!.AccessContextId,
                }));

        var summary = await owners
            .GroupBy(_ => 1)
            .Select(group => new
            {
                MembershipCount = group.Count(owner => owner.ReferenceKind == 1),
                AssignmentCount = group.Count(owner => owner.ReferenceKind == 2),
                HasWrongRoot = group.Any(owner => owner.AccessContextId != accessContextId),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (summary is null ||
            summary.MembershipCount != membershipIds.Count ||
            summary.AssignmentCount != assignmentIds.Count ||
            summary.HasWrongRoot)
        {
            throw WrongRoot();
        }
    }

    private static AccessAuthorityMutationException WrongRoot() =>
        new("Every authority change must belong to the access root whose revision is advancing.");

}

public sealed record WorkspaceAccessGuardResult(
    IReadOnlyCollection<MembershipRoleAssignment> AssignmentEntitiesToValidate,
    IReadOnlyCollection<int> ExistingAssignmentIdsToValidate);
