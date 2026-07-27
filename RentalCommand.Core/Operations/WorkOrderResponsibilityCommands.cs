using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Operations;

public sealed record WorkspaceAccessRevisionExpectation(int AccessContextId, long ExpectedRevision)
    : IAtomicCommandData, IAtomicResultData;

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
    WorkspaceAccessRevisionExpectation[] AccessRevisions) : IAtomicResultData;

public sealed record CloseWorkOrderResponsibilityCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int WorkOrderId,
    Guid ResponsibilityId,
    WorkspaceAccessRevisionExpectation[] AccessRevisionExpectations,
    string Reason,
    string DeliveryIdempotencyKey) : IWorkspaceAccessRevisionSetMutationCommand;

public sealed record CloseWorkOrderResponsibilityResult(
    Guid ResponsibilityId,
    int WorkOrderId,
    DateTime EffectiveToUtc,
    WorkspaceAccessRevisionExpectation[] AccessRevisions) : IAtomicResultData;

/// <summary>
/// The deliberately narrow mutation available to a currently assigned technician. Location,
/// tenant, vendor, cost, title, category, priority, and management fields are intentionally absent.
/// </summary>
public sealed record UpdateAssignedWorkOrderCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorAuthSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int WorkOrderId,
    DateTime ExpectedUpdatedAtUtc,
    WorkOrderStatus? Status,
    string? TechnicianNote,
    DateTime? ScheduledForUtc,
    DateTime? ScheduledWindowEndUtc,
    DateTime? CompletedAtUtc,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum UpdateAssignedWorkOrderOutcome { Applied, Stale }

public sealed record UpdateAssignedWorkOrderResult(
    UpdateAssignedWorkOrderOutcome Outcome,
    int WorkOrderId,
    WorkOrderStatus Status,
    DateTime? ScheduledForUtc,
    DateTime? ScheduledWindowEndUtc,
    DateTime? CompletedAtUtc,
    DateTime UpdatedAtUtc) : IAtomicResultData;
