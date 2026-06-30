using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class WorkOrder : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? TenantId { get; set; }
    public int? LeaseId { get; set; }
    public int? VendorId { get; set; }
    public int? RecurringMaintenanceTaskId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.New;
    public DateTime RequestedAt { get; set; }
    public DateTime? ScheduledFor { get; set; }

    /// <summary>
    /// End of the scheduled arrival window (the visit/appointment is expected between
    /// <see cref="ScheduledFor"/> and this time). Null when no window end was given.
    /// </summary>
    public DateTime? ScheduledWindowEnd { get; set; }

    public DateTime? CompletedAt { get; set; }
    public decimal? EstimatedCost { get; set; }
    public decimal? ActualCost { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }   // soft-delete (preserves maintenance history)

    /// <summary>
    /// JSON object (stored as jsonb) holding the full scan extraction superset for work orders created
    /// from a scan draft. Null for manually created work orders.
    /// </summary>
    public string? ExtractedData { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Tenant? Tenant { get; set; }
    public Lease? Lease { get; set; }
    public Vendor? Vendor { get; set; }
    public RecurringMaintenanceTask? RecurringMaintenanceTask { get; set; }
    public List<Expense> Expenses { get; set; } = [];

    /// <summary>Append-only status timeline (Received → Assigned → In Progress → Done), oldest first.</summary>
    public List<WorkOrderStatusEvent> StatusEvents { get; set; } = [];
}
