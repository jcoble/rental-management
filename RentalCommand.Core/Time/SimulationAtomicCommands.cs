using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Time;

public interface ISimulationAtomicCommand : IAtomicCommandData
{
    int PortfolioId { get; }
    int ActorUserId { get; }

    [AtomicFingerprintIgnore]
    Guid AuthSessionId { get; }

    [AtomicFingerprintIgnore]
    int AccessContextId { get; }

    [AtomicFingerprintIgnore]
    long ExpectedAccessRevision { get; }

}

public sealed record SetSimulationClockCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    DateTime SimAnchorUtc,
    ClockMode Mode,
    bool TimeZoneIdSpecified,
    string? TimeZoneId) : ISimulationAtomicCommand;

public sealed record AdvanceSimulationClockCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    int Days,
    int Hours,
    int Minutes,
    int Seconds) : ISimulationAtomicCommand;

public sealed record FreezeSimulationClockCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision) : ISimulationAtomicCommand;

public sealed record UnfreezeSimulationClockCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision) : ISimulationAtomicCommand;

public sealed record ResetSimulationClockCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision) : ISimulationAtomicCommand;

public sealed record SimulationClockMutationResult(
    DateTime SimNowUtc,
    string Mode,
    string? TimeZoneId,
    double OffsetSeconds);

public sealed record EnqueueSimulationWorkerCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    Guid CommandId,
    string WorkerKey) : ISimulationAtomicCommand;

public sealed record EnqueueSimulationWorkerResult(
    Guid CommandId,
    string WorkerKey,
    string Status,
    DateTime RequestedSimUtc,
    DateTime CreatedRealUtc);
