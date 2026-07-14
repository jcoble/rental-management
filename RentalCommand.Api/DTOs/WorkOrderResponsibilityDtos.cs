using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed record WorkOrderResponsibilityRevisionDto(
    int AccessContextId,
    long ExpectedRevision);

public sealed class AssignWorkOrderResponsibilityRequest
{
    public int WorkspaceMembershipId { get; set; }
    public int MembershipRoleAssignmentId { get; set; }
    public WorkOrderResponsibilityKind Kind { get; set; }
    public Guid? ExpectedCurrentPrimaryResponsibilityId { get; set; }
    public WorkOrderResponsibilityRevisionDto[] AccessRevisionExpectations { get; set; } = [];

    [Required, MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class CloseWorkOrderResponsibilityRequest
{
    public WorkOrderResponsibilityRevisionDto[] AccessRevisionExpectations { get; set; } = [];

    [Required, MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;
}

public sealed record WorkOrderResponsibilityCandidateDto(
    int WorkspaceMembershipId,
    int MembershipRoleAssignmentId,
    int AccessContextId,
    long AccessRevision,
    string MemberDisplayName,
    string RoleProfileName);

public sealed record WorkOrderResponsibilityDto(
    Guid Id,
    int WorkOrderId,
    int? WorkspaceMembershipId,
    int? MembershipRoleAssignmentId,
    int? AccessContextId,
    string MemberDisplayName,
    string RoleProfileName,
    WorkOrderResponsibilityKind Kind,
    DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc,
    string? AssignedReason,
    DateTime? AssignedAtUtc,
    string? EndedReason,
    DateTime? EndedAtUtc,
    bool CanManage);

public sealed class UpdateAssignedWorkOrderRequest
{
    public DateTime ExpectedUpdatedAtUtc { get; set; }

    [EnumDataType(typeof(WorkOrderStatus))]
    public WorkOrderStatus? Status { get; set; }

    [MaxLength(2000)]
    public string? TechnicianNote { get; set; }

    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? ScheduledWindowEnd { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
