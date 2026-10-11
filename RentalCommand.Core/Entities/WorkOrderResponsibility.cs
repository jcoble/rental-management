using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Append-preserved assignment history for one maintenance Team member and one work order.
/// Reassignment closes the effective period; rows are never replaced or deleted.
/// </summary>
public sealed class WorkOrderResponsibility : IPortfolioScoped
{
    public Guid Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int WorkOrderId { get; set; }
    public int WorkspaceMembershipId { get; set; }
    public int MembershipRoleAssignmentId { get; set; }
    public WorkOrderResponsibilityKind Kind { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public int AssignedByUserId { get; set; }
    public int AssignedByAccessContextId { get; set; }
    public string AssignedReason { get; set; } = string.Empty;
    public DateTime AssignedAtUtc { get; set; }
    public int? EndedByUserId { get; set; }
    public int? EndedByAccessContextId { get; set; }
    public string? EndedReason { get; set; }
    public DateTime? EndedAtUtc { get; set; }

    public WorkOrder? WorkOrder { get; set; }
    public WorkspaceMembership? WorkspaceMembership { get; set; }
    public MembershipRoleAssignment? MembershipRoleAssignment { get; set; }
    public ApplicationUser? AssignedByUser { get; set; }
    public WorkspaceAccessContext? AssignedByAccessContext { get; set; }
    public ApplicationUser? EndedByUser { get; set; }
    public WorkspaceAccessContext? EndedByAccessContext { get; set; }
    public List<TechnicianWorkEntry> TechnicianEntries { get; set; } = [];
}
