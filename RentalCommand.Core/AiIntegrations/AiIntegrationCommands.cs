using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.AiIntegrations;

public interface IAiIntegrationActorCommand : IAtomicCommandData
{
    int ActorUserId { get; }
    [AtomicFingerprintIgnore] Guid ActorAuthSessionId { get; }
    [AtomicFingerprintIgnore] int ActorAccessContextId { get; }
    [AtomicFingerprintIgnore] long ActorAccessRevision { get; }
}

public sealed record AiIntegrationStatusResult(
    bool Configured,
    string? Provider,
    string? ModelId,
    DateTime? LastTestedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record ActivateWorkspaceLlmCredentialCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    string Provider,
    string ModelId,
    string ApiKeyIntentDigest,
    [property: AtomicFingerprintIgnore] string ApiKeyCipherText,
    [property: AtomicFingerprintIgnore] DateTime TestedAtUtc) : IAiIntegrationActorCommand;

public sealed record RotateWorkspaceLlmCredentialCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    string Provider,
    string ModelId,
    string ApiKeyIntentDigest,
    [property: AtomicFingerprintIgnore] string ApiKeyCipherText,
    [property: AtomicFingerprintIgnore] DateTime TestedAtUtc) : IAiIntegrationActorCommand;

public sealed record RemoveWorkspaceLlmCredentialCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    [property: AtomicFingerprintIgnore] DateTime RemovedAtUtc) : IAiIntegrationActorCommand;

public sealed record RemoveWorkspaceLlmCredentialResult(
    bool Removed);

public sealed record RecordLlmUsageEvidenceCommand(
    int PortfolioId,
    string UsageEventIdentity,
    string Provider,
    string ModelId,
    string Feature,
    int LatencyMilliseconds,
    int InputUnits,
    int OutputUnits,
    decimal EstimatedCostUsd,
    [property: AtomicFingerprintIgnore] DateTime OccurredAtUtc) : IAtomicCommandData;

public sealed record RecordLlmUsageEvidenceResult(
    long EvidenceId);

public sealed record PortfolioQaDeliveryCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    string Question,
    string Answer,
    string? ToEmail,
    string? ToSms,
    string EmailDeliveryIdempotencyKey,
    string SmsDeliveryIdempotencyKey,
    [property: AtomicFingerprintIgnore] DateTime CreatedAtUtc) : IAiIntegrationActorCommand;

public sealed record PortfolioQaDeliveryResult(
    IReadOnlyList<string> DeliveredChannels);
