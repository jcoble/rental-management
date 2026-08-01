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
    [AtomicFingerprintIgnore] Guid ActorAuthSessionId { get; }
    [AtomicFingerprintIgnore] int ActorAccessContextId { get; }
    [AtomicFingerprintIgnore] long ActorAccessRevision { get; }
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
    DateTime EffectiveFromUtc,
    string WebBaseUrl) : IWorkspaceTeamAuthorityCommand;

/// <summary>
/// Anonymous, one-use completion of the account shell created by a Team invitation. The raw
/// invitation token and password never enter the atomic command or its durable receipt.
/// </summary>
public sealed record ActivateWorkspaceInvitationCommand(
    long InvitationId,
    int InvitedUserId,
    string TokenHash,
    [property: AtomicFingerprintIgnore] string PasswordHash,
    [property: AtomicFingerprintIgnore] string NewSecurityStamp,
    [property: AtomicFingerprintIgnore] string NewConcurrencyStamp) : IAtomicCommandData;

public enum ActivateWorkspaceInvitationOutcome
{
    Activated = 1,
    Invalid = 2,
}

public sealed record ActivateWorkspaceInvitationResult(
    ActivateWorkspaceInvitationOutcome Outcome,
    int UserId,
    int PortfolioId,
    int AccessContextId);

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
    bool RequiresAccountActivation);

public sealed record WorkspaceTeamMutationResult(
    int AccessContextId,
    int WorkspaceMembershipId,
    int? AssignmentId,
    long AccessRevision,
    WorkspaceAccessContextStatus ContextStatus,
    WorkspaceMembershipStatus MembershipStatus);
