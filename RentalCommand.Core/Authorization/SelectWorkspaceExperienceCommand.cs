using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Authorization;

/// <summary>
/// Persists one caller-selected workspace experience against the exact canonical session and
/// access revision that authorized the request.
/// </summary>
public sealed record SelectWorkspaceExperienceCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    WorkspaceExperience Experience) : IAtomicCommandData;

/// <summary>Receipt-safe result for one workspace-experience selection.</summary>
public sealed record SelectWorkspaceExperienceResult(
    WorkspaceExperience Experience) : IAtomicResultData;
