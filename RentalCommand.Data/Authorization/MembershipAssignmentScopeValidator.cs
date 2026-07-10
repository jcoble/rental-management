using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Validates the cross-row scope invariant with one database-side EXISTS. PostgreSQL CHECK
/// constraints cannot reference child rows, so assignment mutation commands call this validator
/// after flushing their selected-scope rows inside the command transaction and before commit. The
/// final baseline may additionally enforce the same invariant with a deferred constraint trigger.
/// </summary>
public sealed class MembershipAssignmentScopeValidator : IMembershipAssignmentScopeValidator
{
    private readonly RentalCommandDbContext _db;

    public MembershipAssignmentScopeValidator(RentalCommandDbContext db) => _db = db;

    public async Task ValidateAsync(
        int assignmentId,
        MembershipRoleAssignmentScopeKind scopeKind,
        CancellationToken cancellationToken = default)
    {
        var hasSelectedScope = await _db.MembershipRoleAssignmentProperties
            .AsNoTracking()
            .AnyAsync(scope => scope.MembershipRoleAssignmentId == assignmentId, cancellationToken);

        var isValid = scopeKind switch
        {
            MembershipRoleAssignmentScopeKind.SelectedProperties => hasSelectedScope,
            MembershipRoleAssignmentScopeKind.AllProperties => !hasSelectedScope,
            MembershipRoleAssignmentScopeKind.AssignedWorkOrders => !hasSelectedScope,
            _ => false,
        };

        if (!isValid)
        {
            throw new DomainValidationException(
                scopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    ? "A selected-properties assignment must contain at least one property scope."
                    : "Only a selected-properties assignment may contain property scope rows.");
        }
    }
}
