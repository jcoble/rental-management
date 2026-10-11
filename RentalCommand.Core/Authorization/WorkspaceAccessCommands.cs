using RentalCommand.Core.Atomic;
namespace RentalCommand.Core.Authorization;

/// <summary>
/// Identifies a workspace-authority mutation so its authorization handler can enforce the
/// access-root revision and assignment-scope invariants before the transaction commits.
/// </summary>
public interface IWorkspaceAccessMutationCommand : IAtomicCommandData
{
    [AtomicFingerprintIgnore] int AccessContextId { get; }
    long ExpectedRevision { get; }
}

/// <summary>Receipt-safe result returned identically for an executed or replayed access command.</summary>
public sealed record WorkspaceAccessMutationResult(
    int AccessContextId,
    int AssignmentId,
    long AccessRevision);
