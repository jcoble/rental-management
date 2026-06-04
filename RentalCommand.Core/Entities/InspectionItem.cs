using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One checklist line on a smart inspection: a labelled item within an area/room that the inspector
/// marks Pass/Fail/NotApplicable (defaulting to <see cref="InspectionItemResult.Pending"/>), optionally
/// with a note and a photo. On <c>Complete</c>, every <see cref="InspectionItemResult.Fail"/> item spawns
/// a <see cref="WorkOrder"/> whose id is recorded in <see cref="SpawnedWorkOrderId"/>.
/// </summary>
public class InspectionItem
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int InspectionId { get; set; }

    /// <summary>Room/area grouping, e.g. "Kitchen", "Bathroom", "Safety".</summary>
    public string Area { get; set; } = string.Empty;

    /// <summary>The thing being checked, e.g. "Sink &amp; faucet", "Smoke detectors".</summary>
    public string Label { get; set; } = string.Empty;

    public InspectionItemResult Result { get; set; } = InspectionItemResult.Pending;

    public string? Note { get; set; }

    /// <summary>Optional photo evidence — a <see cref="StoredFile"/> attached to this item.</summary>
    public int? PhotoStoredFileId { get; set; }

    /// <summary>The work order auto-created for this item when it was marked Fail on completion.</summary>
    public int? SpawnedWorkOrderId { get; set; }

    /// <summary>Display order within the inspection (ascending).</summary>
    public int SortOrder { get; set; }

    public Inspection? Inspection { get; set; }
    public StoredFile? PhotoStoredFile { get; set; }
    public WorkOrder? SpawnedWorkOrder { get; set; }
}
