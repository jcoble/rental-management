using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Operations;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Proves that an assignment/reassignment advances every affected member access root exactly once
/// in the same tracked command. The affected-member ownership check remains one SQL query.
/// </summary>
public sealed class WorkOrderResponsibilityAccessRevisionGuard
{
    public async Task ValidatePendingMutationAsync(
        RentalCommandDbContext db,
        IReadOnlyCollection<WorkspaceAccessRevisionExpectation> expectations,
        CancellationToken ct)
    {
        if (expectations.GroupBy(item => item.AccessContextId).Any(group => group.Count() != 1))
            throw new AccessAuthorityMutationException("Affected access revisions must be unique.");

        var expected = expectations
            .OrderBy(item => item.AccessContextId)
            .ToArray();
        if (expected.Length == 0 || expected.Any(item => item.AccessContextId <= 0 || item.ExpectedRevision <= 0))
            throw new AccessAuthorityMutationException("Affected access revisions are required.");

        var roots = db.ChangeTracker.Entries<WorkspaceAccessContext>()
            .Where(entry => entry.State == EntityState.Modified)
            .OrderBy(entry => entry.Entity.Id)
            .ToArray();
        if (roots.Length != expected.Length)
            throw new AccessAuthorityMutationException("Every and only affected access root must advance.");

        for (var index = 0; index < expected.Length; index++)
        {
            var root = roots[index];
            var expectation = expected[index];
            var revision = root.Property(item => item.AccessRevision);
            if (root.Entity.Id != expectation.AccessContextId ||
                revision.OriginalValue != expectation.ExpectedRevision ||
                revision.CurrentValue != checked(expectation.ExpectedRevision + 1) ||
                !revision.IsModified)
            {
                throw new AccessAuthorityMutationException(
                    "Each affected access revision must advance exactly once from its expected value.");
            }
        }

        var responsibilityEntries = db.ChangeTracker.Entries<WorkOrderResponsibility>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToArray();
        if (responsibilityEntries.Length == 0 ||
            responsibilityEntries.Any(entry => entry.State == EntityState.Modified &&
                entry.Property(item => item.EffectiveToUtc).CurrentValue is null))
        {
            throw new AccessAuthorityMutationException(
                "A responsibility authority change must insert a new period or close a current period.");
        }

        var membershipIds = responsibilityEntries
            .Select(entry => entry.Entity.WorkspaceMembershipId)
            .Distinct()
            .ToArray();
        var expectedContextIds = expected.Select(item => item.AccessContextId).ToArray();
        var ownership = await db.WorkspaceMemberships
            .AsNoTracking()
            .Where(membership => membershipIds.Contains(membership.Id))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                HasUnexpectedOwner = group.Any(membership =>
                    !expectedContextIds.Contains(membership.AccessContextId)),
            })
            .SingleOrDefaultAsync(ct);
        if (ownership is null || ownership.Count != membershipIds.Length || ownership.HasUnexpectedOwner)
            throw new AccessAuthorityMutationException(
                "Every changed responsibility must belong to an affected access root.");
    }
}
