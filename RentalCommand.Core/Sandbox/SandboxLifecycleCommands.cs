using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Sandbox;

public enum SandboxOnboardingChoice
{
    Sandbox = 1,
    Live = 2,
}

public enum SandboxLifecycleOperation
{
    ApplyOnboardingChoice = 1,
    GoLive = 2,
}

public sealed record SandboxLifecycleCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    SandboxLifecycleOperation Operation,
    SandboxOnboardingChoice? OnboardingChoice,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record SandboxLifecycleResult(
    bool PortfolioFound,
    int PortfolioId,
    bool IsSandbox,
    DateTime? SandboxSeededAtUtc,
    bool OnboardingChoicePending);
