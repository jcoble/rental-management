using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One dispatch of a work order to a vendor by SMS: the app texted the vendor the job and asked them
/// to reply DONE when finished. A single OPEN dispatch (<see cref="VendorDispatchStatus.Dispatched"/>
/// or <see cref="VendorDispatchStatus.Acknowledged"/>) exists per (work order, vendor) at a time; an
/// inbound "DONE" from the vendor's phone closes it and the work order. Drives the response-time leg
/// of the vendor scorecard (<see cref="DispatchedAtUtc"/> → <see cref="RespondedAtUtc"/>).
/// </summary>
public class VendorDispatch
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int WorkOrderId { get; set; }
    public int VendorId { get; set; }

    public VendorDispatchStatus Status { get; set; } = VendorDispatchStatus.Dispatched;

    public DateTime DispatchedAtUtc { get; set; }

    /// <summary>When the vendor replied (DONE/acknowledged). Null while the dispatch is still open.</summary>
    public DateTime? RespondedAtUtc { get; set; }

    /// <summary>The outbound SMS body sent to the vendor, kept for the audit/history.</summary>
    public string? Message { get; set; }

    public Portfolio? Portfolio { get; set; }
    public WorkOrder? WorkOrder { get; set; }
    public Vendor? Vendor { get; set; }
}
