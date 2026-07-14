using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One independently scoped job assignment. Capability and resource scope must be evaluated from
/// this same row; callers must never union capabilities across assignments before checking scope.
/// </summary>
public sealed class MembershipRoleAssignment
{
    public int Id { get; set; }
    public int WorkspaceMembershipId { get; set; }
    public int PortfolioId { get; set; }
    public int RoleProfileId { get; set; }
    public MembershipRoleAssignmentStatus Status { get; set; } = MembershipRoleAssignmentStatus.Active;
    public MembershipRoleAssignmentScopeKind ScopeKind { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public WorkspaceMembership? WorkspaceMembership { get; set; }
    public RoleProfile? RoleProfile { get; set; }
    public ICollection<MembershipRoleAssignmentProperty> SelectedProperties { get; set; } =
        new List<MembershipRoleAssignmentProperty>();
    public ICollection<WorkOrderResponsibility> WorkOrderResponsibilities { get; set; } =
        new List<WorkOrderResponsibility>();
}
