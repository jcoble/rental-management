using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Authorization;

public interface IOwnerRelationshipAccessCommand : IAtomicCommandData
{
    int PortfolioId { get; }
    int OwnerEntityId { get; }
    int TargetAccessContextId { get; }
    long ExpectedTargetAccessRevision { get; }
    string Reason { get; }
    int ActorUserId { get; }
    Guid ActorAuthSessionId { get; }
    int ActorAccessContextId { get; }
    long ActorAccessRevision { get; }
}

public sealed record GrantOwnerUserAccessCommand(
    int PortfolioId,
    int OwnerEntityId,
    int TargetAccessContextId,
    long ExpectedTargetAccessRevision,
    DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc,
    string Reason,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision) : IOwnerRelationshipAccessCommand;

public sealed record ActivateOwnerPortalAccessCommand(
    int PortfolioId,
    int OwnerEntityId,
    DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc,
    string Reason,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    Guid EmailLockId,
    string WebBaseUrl) : IWorkspaceTeamAuthorityCommand;

public sealed record RevokeOwnerUserAccessCommand(
    int PortfolioId,
    int OwnerEntityId,
    int TargetAccessContextId,
    long ExpectedTargetAccessRevision,
    int OwnerUserAccessId,
    string Reason,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision) : IOwnerRelationshipAccessCommand;

public enum OwnerRelationshipAccessMutationOutcome
{
    Applied,
    AlreadyActive,
    AlreadyRevoked,
    Invalid,
    NotFound,
}

public enum ActivateOwnerPortalAccessMutationOutcome
{
    Activated,
    InvitationPending,
    AlreadyActive,
    MissingOwnerEmail,
    InactiveWorkspaceAccess,
    PrimaryOwnerNotSupported,
    Invalid,
    NotFound,
}

public sealed record OwnerRelationshipAccessMutationResult(
    OwnerRelationshipAccessMutationOutcome Outcome,
    int OwnerEntityId,
    int TargetAccessContextId,
    int? OwnerUserAccessId,
    long AccessRevision) : IAtomicResultData;

public sealed record ActivateOwnerPortalAccessMutationResult(
    ActivateOwnerPortalAccessMutationOutcome Outcome,
    int OwnerEntityId,
    string? OwnerEmail,
    int? UserId,
    int? TargetAccessContextId,
    int? OwnerUserAccessId,
    long? AccessRevision,
    bool RequiresAccountActivation,
    DateTime? InvitationExpiresAtUtc) : IAtomicResultData;
