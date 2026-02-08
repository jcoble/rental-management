using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class WorkOrder
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? TenantId { get; set; }
    public int? LeaseId { get; set; }
    public int? VendorId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.New;
    public DateTime RequestedAt { get; set; }
    public DateTime? ScheduledFor { get; set; }
    public DateTime? CompletedAt { get; set; }
    public decimal? EstimatedCost { get; set; }
    public decimal? ActualCost { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Tenant? Tenant { get; set; }
    public Lease? Lease { get; set; }
    public Vendor? Vendor { get; set; }
    public List<Expense> Expenses { get; set; } = [];
}
