using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed record TeamMemberSummaryDto(
    int UserId,
    int AccessContextId,
    int WorkspaceMembershipId,
    string Email,
    string DisplayName,
    WorkspaceAccessContextStatus AccessStatus,
    WorkspaceMembershipStatus MembershipStatus,
    long AccessRevision,
    int AssignmentCount,
    string RoleSummary,
    DateTime CreatedAtUtc);

public sealed record TeamMemberPageDto(
    IReadOnlyList<TeamMemberSummaryDto> Items,
    int TotalCount,
    int Skip,
    int Take);

public sealed record TeamAssignmentSummaryDto(
    int AssignmentId,
    string RoleProfileKey,
    string RoleProfileName,
    MembershipRoleAssignmentStatus Status,
    MembershipRoleAssignmentScopeKind ScopeKind,
    int SelectedPropertyCount,
    DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc);

public sealed record TeamAssignmentPageDto(
    IReadOnlyList<TeamAssignmentSummaryDto> Items,
    int TotalCount,
    int Skip,
    int Take);

public sealed record TeamRoleProfileDto(
    string Key,
    string DisplayName,
    string Description,
    WorkspaceExperience DefaultExperience,
    MembershipRoleAssignmentScopeKind DefaultScopeKind);

public sealed class CreateWorkspaceMembershipRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string DisplayName { get; set; } = string.Empty;
    [Required, MaxLength(80)] public string RoleProfileKey { get; set; } = string.Empty;
    public MembershipRoleAssignmentScopeKind ScopeKind { get; set; }
    public int[] SelectedPropertyIds { get; set; } = [];
    public DateTime EffectiveFromUtc { get; set; }
}

public sealed class AddWorkspaceRoleAssignmentRequest
{
    public long ExpectedAccessRevision { get; set; }
    [Required, MaxLength(80)] public string RoleProfileKey { get; set; } = string.Empty;
    public MembershipRoleAssignmentScopeKind ScopeKind { get; set; }
    public int[] SelectedPropertyIds { get; set; } = [];
    public DateTime EffectiveFromUtc { get; set; }
}

public sealed class EndWorkspaceRoleAssignmentRequest
{
    public long ExpectedAccessRevision { get; set; }
    public DateTime EffectiveToUtc { get; set; }
}

public sealed class ReplaceWorkspaceAssignmentPropertyScopeRequest
{
    public long ExpectedAccessRevision { get; set; }
    public int[] PropertyIds { get; set; } = [];
}

public sealed class ChangeWorkspaceMembershipStatusRequest
{
    public long ExpectedAccessRevision { get; set; }
    public WorkspaceMembershipStatusAction Action { get; set; }
}
