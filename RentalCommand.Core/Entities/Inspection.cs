using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class Inspection
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? LeaseId { get; set; }
    public InspectionType Type { get; set; } = InspectionType.Routine;
    public InspectionStatus Status { get; set; } = InspectionStatus.Scheduled;
    public DateTime ScheduledFor { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Outcome { get; set; }
    public string? Notes { get; set; }

    /// <summary>The template this inspection was started from, when started as a smart checklist.</summary>
    public int? TemplateId { get; set; }

    /// <summary>The generated PDF report (a <see cref="StoredFile"/>), set on completion.</summary>
    public int? ReportStoredFileId { get; set; }

    /// <summary>Optional name of the person who performed the inspection (shown on the report).</summary>
    public string? Inspector { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Lease? Lease { get; set; }

    /// <summary>Checklist items, ordered by <see cref="InspectionItem.SortOrder"/>.</summary>
    public List<InspectionItem> Items { get; set; } = [];
}
