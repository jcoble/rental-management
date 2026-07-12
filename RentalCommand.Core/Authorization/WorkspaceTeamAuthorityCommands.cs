using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Authorization;

/// <summary>
/// Authentication and authorization coordinates carried by every Team authority command. They are
/// revalidated from PostgreSQL both on first execution and receipt replay; no Identity role or JWT
/// role claim participates in the decision.
/// </summary>
public interface IWorkspaceTeamAuthorityCommand : IAtomicCommandData
{
    int PortfolioId { get; }
    int ActorUserId { get; }
    Guid ActorAuthSessionId { get; }
    int ActorAccessContextId { get; }
    long ActorAccessRevision { get; }
}

public sealed record CreateWorkspaceMembershipCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    string Email,
    string DisplayName,
    string RoleProfileKey,
    MembershipRoleAssignmentScopeKind ScopeKind,
    int[] SelectedPropertyIds,
    DateTime EffectiveFromUtc) : IWorkspaceTeamAuthorityCommand;

public sealed record AddWorkspaceRoleAssignmentCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int TargetAccessContextId,
    long ExpectedRevision,
    string RoleProfileKey,
    MembershipRoleAssignmentScopeKind ScopeKind,
    int[] SelectedPropertyIds,
    DateTime EffectiveFromUtc) : IWorkspaceTeamAuthorityCommand, IWorkspaceAccessMutationCommand
{
    int IWorkspaceAccessMutationCommand.AccessContextId => TargetAccessContextId;
}

public sealed record EndWorkspaceRoleAssignmentCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int TargetAccessContextId,
    long ExpectedRevision,
    int AssignmentId,
    DateTime EffectiveToUtc) : IWorkspaceTeamAuthorityCommand, IWorkspaceAccessMutationCommand
{
    int IWorkspaceAccessMutationCommand.AccessContextId => TargetAccessContextId;
}

public sealed record ReplaceWorkspaceAssignmentPropertyScopeCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int TargetAccessContextId,
    long ExpectedRevision,
    int AssignmentId,
    int[] SelectedPropertyIds) : IWorkspaceTeamAuthorityCommand, IWorkspaceAccessMutationCommand
{
    int IWorkspaceAccessMutationCommand.AccessContextId => TargetAccessContextId;
}

public enum WorkspaceMembershipStatusAction
{
    Suspend = 1,
    Reactivate = 2,
    Revoke = 3,
}

public sealed record ChangeWorkspaceMembershipStatusCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int TargetAccessContextId,
    long ExpectedRevision,
    WorkspaceMembershipStatusAction Action) : IWorkspaceTeamAuthorityCommand, IWorkspaceAccessMutationCommand
{
    int IWorkspaceAccessMutationCommand.AccessContextId => TargetAccessContextId;
}

public sealed record CreateWorkspaceMembershipResult(
    int UserId,
    int AccessContextId,
    int WorkspaceMembershipId,
    int AssignmentId,
    long AccessRevision,
    bool RequiresAccountActivation) : IAtomicResultData;

public sealed record WorkspaceTeamMutationResult(
    int AccessContextId,
    int WorkspaceMembershipId,
    int? AssignmentId,
    long AccessRevision,
    WorkspaceAccessContextStatus ContextStatus,
    WorkspaceMembershipStatus MembershipStatus) : IAtomicResultData;
