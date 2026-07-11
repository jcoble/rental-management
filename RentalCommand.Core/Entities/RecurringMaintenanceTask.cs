using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A standing maintenance chore the landlord configures once (e.g. "HVAC filter every 90 days",
/// "quarterly gutter cleaning"). The Engine's recurring-maintenance worker auto-creates a
/// <see cref="WorkOrder"/> each period the task comes due, then advances <see cref="NextDueDate"/>
/// by the <see cref="RecurrenceInterval"/>. The per-task <see cref="IsActive"/> flag is the real
/// on/off switch — deactivating it stops generation without losing the chore's history.
/// </summary>
public class RecurringMaintenanceTask
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>The property the generated work orders are filed against (required).</summary>
    public int PropertyId { get; set; }

    /// <summary>Optional unit the chore is scoped to (e.g. a specific apartment's filter).</summary>
    public int? UnitId { get; set; }

    /// <summary>Optional preferred vendor pre-assigned onto each generated work order.</summary>
    public int? VendorId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Free-text work-order category, e.g. "HVAC", "Plumbing", "Landscaping".</summary>
    public string? Category { get; set; }

    public RecurrenceInterval RecurrenceInterval { get; set; } = RecurrenceInterval.Monthly;

    /// <summary>
    /// The next date (the landlord's local calendar) this chore is due. When it is on/before
    /// "today" the worker generates a work order and advances this by the interval.
    /// </summary>
    public DateTime NextDueDate { get; set; }

    /// <summary>
    /// Optional local time of day to schedule the generated work order on <see cref="NextDueDate"/>.
    /// Null means the generated work order is unscheduled.
    /// </summary>
    public TimeOnly? ScheduledTime { get; set; }

    /// <summary>Expected cost copied to each generated work order and used for recurring budget views.</summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>UTC timestamp of the most recent auto-generation; null until the first run.</summary>
    public DateTime? LastGeneratedAtUtc { get; set; }

    /// <summary>The on/off switch. Inactive tasks are skipped by the worker (no generation).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Priority stamped onto each generated work order.</summary>
    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; the global query filter hides deleted tasks from every read.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Short-lived Engine ownership for recurring-maintenance generation.</summary>
    public string? WorkerClaimOwner { get; set; }
    public Guid? WorkerClaimToken { get; set; }
    public DateTime? WorkerClaimExpiresAtUtc { get; set; }
    public int WorkerClaimAttemptCount { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Vendor? Vendor { get; set; }
    public List<WorkOrder> WorkOrders { get; set; } = [];
}
