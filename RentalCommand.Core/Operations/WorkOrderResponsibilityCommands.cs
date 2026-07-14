using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Operations;

public sealed record WorkspaceAccessRevisionExpectation(int AccessContextId, long ExpectedRevision);

/// <summary>Marker for one authority command that must advance several affected access roots once.</summary>
public interface IWorkspaceAccessRevisionSetMutationCommand : IAtomicCommandData
{
    WorkspaceAccessRevisionExpectation[] AccessRevisionExpectations { get; }
}

public sealed record AssignWorkOrderResponsibilityCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int WorkOrderId,
    int WorkspaceMembershipId,
    int MembershipRoleAssignmentId,
    WorkOrderResponsibilityKind Kind,
    Guid? ExpectedCurrentPrimaryResponsibilityId,
    WorkspaceAccessRevisionExpectation[] AccessRevisionExpectations,
    string Reason,
    string DeliveryIdempotencyKey) : IWorkspaceAccessRevisionSetMutationCommand;

public sealed record AssignWorkOrderResponsibilityResult(
    Guid ResponsibilityId,
    int WorkOrderId,
    int WorkspaceMembershipId,
    int MembershipRoleAssignmentId,
    WorkOrderResponsibilityKind Kind,
    DateTime EffectiveFromUtc,
    IReadOnlyDictionary<int, long> AccessRevisions);
