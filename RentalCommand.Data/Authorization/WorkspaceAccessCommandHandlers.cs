using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Typed, data-only access mutation. It loads the root and exact assignment with bounded SQL queries;
/// no tracked authority graph or caller-provided delegate crosses the command boundary.
/// </summary>
public sealed class ChangeWorkspaceAssignmentScopeHandler
    : IAtomicCommandHandler<ChangeWorkspaceAssignmentScopeCommand, WorkspaceAccessMutationResult>
{
    public async Task<WorkspaceAccessMutationResult> HandleAsync(
        ChangeWorkspaceAssignmentScopeCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var accessContext = await LoadRootAsync(attempt, command.AccessContextId, ct);
        EnsureExpectedRevision(accessContext, command.ExpectedRevision);

        var assignment = await attempt.Persistence.Query<MembershipRoleAssignment>()
            .Where(item => item.Id == command.AssignmentId &&
                           item.WorkspaceMembership!.AccessContextId == command.AccessContextId)
            .Select(item => new AssignmentMutationTarget(
                item,
                item.RoleProfile!.Key,
                item.SelectedProperties.Count()))
            .SingleOrDefaultAsync(ct)
            ?? throw WrongRoot();

        ValidateScope(assignment.RoleProfileKey, command.ScopeKind, assignment.SelectedPropertyCount,
            command.AssignmentId);

        assignment.Entity.ScopeKind = command.ScopeKind;
        assignment.Entity.UpdatedAtUtc = command.ChangedAtUtc;
        accessContext.UpdatedAtUtc = command.ChangedAtUtc;
        accessContext.AdvanceRevision(command.ExpectedRevision);

        attempt.StageSemanticEvent(Audit(
            accessContext.PortfolioId,
            command.AssignmentId,
            "Assignment scope changed",
            new { command.ScopeKind, Revision = command.ExpectedRevision + 1 }));

        return new WorkspaceAccessMutationResult(
            command.AccessContextId,
            command.AssignmentId,
            command.ExpectedRevision + 1);
    }

    private static void ValidateScope(
        string roleProfileKey,
        MembershipRoleAssignmentScopeKind scopeKind,
        int selectedPropertyCount,
        int assignmentId)
    {
        var roleAllowsScope = roleProfileKey switch
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

        if (!roleAllowsScope)
        {
            throw new DomainValidationException(
                $"Role {roleProfileKey} cannot use {scopeKind} assignment scope.");
        }

        if (scopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties && selectedPropertyCount == 0)
        {
            throw new DomainValidationException(
                $"Assignment {assignmentId} must contain at least one selected property.");
        }

        if (scopeKind != MembershipRoleAssignmentScopeKind.SelectedProperties && selectedPropertyCount != 0)
        {
            throw new DomainValidationException(
                $"Assignment {assignmentId} cannot contain selected-property rows for {scopeKind} scope.");
        }
    }

    private sealed record AssignmentMutationTarget(
        MembershipRoleAssignment Entity,
        string RoleProfileKey,
        int SelectedPropertyCount);

    internal static async Task<WorkspaceAccessContext> LoadRootAsync(
        IAtomicWriteAttempt attempt,
        int accessContextId,
        CancellationToken ct) =>
        await attempt.Persistence.Query<WorkspaceAccessContext>()
            .SingleOrDefaultAsync(context => context.Id == accessContextId, ct)
        ?? throw new AccessContextUnavailableException();

    internal static void EnsureExpectedRevision(WorkspaceAccessContext context, long expectedRevision)
    {
        if (context.AccessRevision != expectedRevision)
        {
            throw new StaleAccessRevisionException(expectedRevision, context.AccessRevision);
        }
    }

    internal static AtomicSemanticAudit Audit(
        int portfolioId,
        int assignmentId,
        string reason,
        object values) =>
        new(
            portfolioId,
            nameof(MembershipRoleAssignment),
            assignmentId,
            AuditLogOperation.Updated,
            NewValues: JsonSerializer.Serialize(values),
            ChangeReason: reason);

    internal static AccessAuthorityMutationException WrongRoot() =>
        new("The assignment does not belong to the access root whose revision is advancing.");
}

public sealed class ChangeWorkspaceAssignmentEndHandler
    : IAtomicCommandHandler<ChangeWorkspaceAssignmentEndCommand, WorkspaceAccessMutationResult>
{
    public async Task<WorkspaceAccessMutationResult> HandleAsync(
        ChangeWorkspaceAssignmentEndCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var accessContext = await ChangeWorkspaceAssignmentScopeHandler.LoadRootAsync(
            attempt, command.AccessContextId, ct);
        ChangeWorkspaceAssignmentScopeHandler.EnsureExpectedRevision(
            accessContext, command.ExpectedRevision);

        var assignment = await attempt.Persistence.Query<MembershipRoleAssignment>()
            .SingleOrDefaultAsync(item => item.Id == command.AssignmentId &&
                                          item.WorkspaceMembership!.AccessContextId == command.AccessContextId,
                ct)
            ?? throw ChangeWorkspaceAssignmentScopeHandler.WrongRoot();

        if (command.EffectiveToUtc is not null && command.EffectiveToUtc <= assignment.EffectiveFromUtc)
        {
            throw new DomainValidationException("Assignment end must be after its effective start.");
        }

        assignment.EffectiveToUtc = command.EffectiveToUtc;
        assignment.UpdatedAtUtc = command.ChangedAtUtc;
        accessContext.UpdatedAtUtc = command.ChangedAtUtc;
        accessContext.AdvanceRevision(command.ExpectedRevision);

        attempt.StageSemanticEvent(ChangeWorkspaceAssignmentScopeHandler.Audit(
            accessContext.PortfolioId,
            command.AssignmentId,
            "Assignment effective end changed",
            new { command.EffectiveToUtc, Revision = command.ExpectedRevision + 1 }));

        return new WorkspaceAccessMutationResult(
            command.AccessContextId,
            command.AssignmentId,
            command.ExpectedRevision + 1);
    }
}
