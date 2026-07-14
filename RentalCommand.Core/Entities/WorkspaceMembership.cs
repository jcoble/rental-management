using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Optional management-business membership beneath an access context. An Owner- or Tenant-only
/// relationship context intentionally has no row here.
/// </summary>
public sealed class WorkspaceMembership
{
    public int Id { get; set; }
    public int AccessContextId { get; set; }
    public int PortfolioId { get; set; }
    public WorkspaceMembershipStatus Status { get; set; } = WorkspaceMembershipStatus.Active;
    public WorkspaceExperience DefaultExperience { get; set; } = WorkspaceExperience.Management;
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public WorkspaceAccessContext? AccessContext { get; set; }
    public ICollection<MembershipRoleAssignment> RoleAssignments { get; set; } =
        new List<MembershipRoleAssignment>();
    public ICollection<WorkspaceInvitation> Invitations { get; set; } =
        new List<WorkspaceInvitation>();
    public ICollection<WorkOrderResponsibility> WorkOrderResponsibilities { get; set; } =
        new List<WorkOrderResponsibility>();
}
