using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Append-only field record authored by the technician responsible for a work order. Quantities are
/// operational facts only: this table deliberately has no rate, price, cost, expense, or vendor field.
/// </summary>
public sealed class TechnicianWorkEntry : IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int WorkOrderId { get; set; }
    public Guid WorkOrderResponsibilityId { get; set; }
    public int WorkspaceMembershipId { get; set; }
    public int MembershipRoleAssignmentId { get; set; }
    public int CreatedByUserId { get; set; }
    public TechnicianWorkEntryKind Kind { get; set; }
    public string? Note { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public int? StoredFileId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public WorkOrder? WorkOrder { get; set; }
    public WorkOrderResponsibility? WorkOrderResponsibility { get; set; }
    public WorkspaceMembership? WorkspaceMembership { get; set; }
    public MembershipRoleAssignment? MembershipRoleAssignment { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public StoredFile? StoredFile { get; set; }
}
