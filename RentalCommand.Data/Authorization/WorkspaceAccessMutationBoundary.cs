using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Transaction-ready, opt-in writer boundary for new access-authority commands. No legacy writer or
/// authentication path is bridged to it in this kernel slice.
/// </summary>
public sealed class WorkspaceAccessMutationBoundary : IWorkspaceAccessMutationBoundary
{
    private readonly RentalCommandDbContext _db;
    private readonly WorkspaceAccessRevisionGuard _revisionGuard;
    private readonly IMembershipAssignmentScopeValidator _scopeValidator;

    public WorkspaceAccessMutationBoundary(
        RentalCommandDbContext db,
        WorkspaceAccessRevisionGuard revisionGuard,
        IMembershipAssignmentScopeValidator scopeValidator)
    {
        _db = db;
        _revisionGuard = revisionGuard;
        _scopeValidator = scopeValidator;
    }

    public async Task ExecuteAsync(
        int accessContextId,
        long expectedRevision,
        Action<WorkspaceAccessContext> stageTrackedChanges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stageTrackedChanges);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var accessContext = await _db.WorkspaceAccessContexts
                .Include(context => context.Membership)
                .ThenInclude(membership => membership!.RoleAssignments)
                .ThenInclude(assignment => assignment.SelectedProperties)
                .SingleOrDefaultAsync(context => context.Id == accessContextId, cancellationToken)
                ?? throw new AccessContextUnavailableException();

            if (accessContext.AccessRevision != expectedRevision)
            {
                throw new StaleAccessRevisionException(expectedRevision, accessContext.AccessRevision);
            }

            stageTrackedChanges(accessContext);
            accessContext.AdvanceRevision(expectedRevision);

            var guardResult = await _revisionGuard.ValidatePendingMutationAsync(
                _db, accessContextId, expectedRevision, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            var assignmentIds = guardResult.AssignmentEntitiesToValidate
                .Select(assignment => assignment.Id)
                .Concat(guardResult.ExistingAssignmentIdsToValidate)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            await _scopeValidator.ValidateAsync(assignmentIds, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
