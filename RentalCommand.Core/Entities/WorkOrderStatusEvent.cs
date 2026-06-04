using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One immutable entry in a <see cref="WorkOrder"/>'s status timeline (Received → Assigned → In
/// Progress → Done). Written on create (an initial <c>null → status</c> event) and on every status
/// change, in the same save as the work-order mutation so the stream never diverges from the current
/// status. Read by landlord/staff (work-order detail) and by the owning tenant (portal detail).
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

    /// <summary>Optional free-text note supplied with the change (e.g. "parts ordered").</summary>
    public string? Note { get; set; }

    /// <summary>Int id of the user who made the change, when an authenticated user did. Null for system writes.</summary>
    public int? ChangedByUserId { get; set; }

    /// <summary>Human label for who/what changed it, e.g. "Staff", "Tenant", "System".</summary>
    public string? ChangedByLabel { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public WorkOrder? WorkOrder { get; set; }
}
