using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One immutable entry in a <see cref="WorkOrder"/>'s activity stream. Status rows are written on
/// create and status changes; comment/edit rows share the same atomic stream so tenant and staff
/// detail can project one canonical timeline without a parallel comment table.
/// </summary>
public class WorkOrderStatusEvent
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int WorkOrderId { get; set; }

    /// <summary>The status the work order moved away from. Null for the initial create event.</summary>
    public WorkOrderStatus? FromStatus { get; set; }

    /// <summary>The status the work order moved into.</summary>
    public WorkOrderStatus ToStatus { get; set; }

    /// <summary>Stable activity category, for example Status, Comment, or Edit.</summary>
    public string Kind { get; set; } = "Status";

    /// <summary>Visibility gate for role-aware detail projection. Public rows are tenant-visible.</summary>
    public string Visibility { get; set; } = "Public";

    /// <summary>Optional free-text note supplied with the change (e.g. "parts ordered").</summary>
    public string? Note { get; set; }

    /// <summary>Int id of the user who made the change, when an authenticated user did. Null for system writes.</summary>
    public int? ChangedByUserId { get; set; }

    /// <summary>Human label for who/what changed it, e.g. "Staff", "Tenant", "System".</summary>
    public string? ChangedByLabel { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Original invalid timestamp preserved when chronology repair moves a display event forward.</summary>
    public DateTime? ChronologyRepairOriginalCreatedAtUtc { get; set; }

    public WorkOrder? WorkOrder { get; set; }
}
