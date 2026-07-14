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

public sealed record WorkOrderResponsibilityDto(
    Guid Id,
    int WorkOrderId,
    int WorkspaceMembershipId,
    int MembershipRoleAssignmentId,
    int AccessContextId,
    string MemberDisplayName,
    string RoleProfileName,
    WorkOrderResponsibilityKind Kind,
    DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc,
    string AssignedReason,
    DateTime AssignedAtUtc,
    string? EndedReason,
    DateTime? EndedAtUtc);
