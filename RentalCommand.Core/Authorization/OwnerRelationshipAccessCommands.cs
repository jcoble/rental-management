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

public sealed record OwnerRelationshipAccessMutationResult(
    OwnerRelationshipAccessMutationOutcome Outcome,
    int OwnerEntityId,
    int TargetAccessContextId,
    int? OwnerUserAccessId,
    long AccessRevision) : IAtomicResultData;
