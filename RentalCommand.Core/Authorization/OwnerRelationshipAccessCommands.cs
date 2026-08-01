using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Authorization;

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

public sealed record RevokeOwnerPortalAccessCommand(
    int PortfolioId,
    int OwnerEntityId,
    string Reason,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    [property: AtomicFingerprintIgnore] DateTime ChangedAtUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IWorkspaceTeamAuthorityCommand;

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

public enum RevokeOwnerPortalAccessMutationOutcome
{
    Revoked,
    AlreadyRevoked,
    NotFound,
}

public sealed record ActivateOwnerPortalAccessMutationResult(
    ActivateOwnerPortalAccessMutationOutcome Outcome,
    int OwnerEntityId,
    string? OwnerEmail,
    int? UserId,
    int? TargetAccessContextId,
    int? OwnerUserAccessId,
    long? AccessRevision,
    bool RequiresAccountActivation,
    DateTime? InvitationExpiresAtUtc);

public sealed record RevokeOwnerPortalAccessMutationResult(
    RevokeOwnerPortalAccessMutationOutcome Outcome,
    int OwnerEntityId,
    string? OwnerEmail,
    int RevokedRelationshipCount,
    int? TargetAccessContextId,
    int? OwnerUserAccessId,
    long? AccessRevision);
