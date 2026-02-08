using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Lease? Lease { get; set; }
}
