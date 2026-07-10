using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Authorization;

/// <summary>
/// Changes the operational scope kind of one existing assignment. Selected-property membership is
/// deliberately managed by a separate future command so a caller cannot smuggle an unbounded object
/// graph or an arbitrary persistence delegate through the authorization boundary.
/// </summary>
public sealed record ChangeWorkspaceAssignmentScopeCommand(
    int AccessContextId,
    long ExpectedRevision,
    int AssignmentId,
    MembershipRoleAssignmentScopeKind ScopeKind,
    DateTime ChangedAtUtc) : IAtomicCommandData;

/// <summary>Changes the effective end of one assignment without changing its owning access root.</summary>
public sealed record ChangeWorkspaceAssignmentEndCommand(
    int AccessContextId,
    long ExpectedRevision,
    int AssignmentId,
    DateTime? EffectiveToUtc,
    DateTime ChangedAtUtc) : IAtomicCommandData;

/// <summary>Receipt-safe result returned identically for an executed or replayed access command.</summary>
public sealed record WorkspaceAccessMutationResult(
    int AccessContextId,
    int AssignmentId,
    long AccessRevision) : IAtomicResultData;
