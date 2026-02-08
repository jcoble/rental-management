using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class Phase
{
    public int Id { get; set; }
    public int MilestoneId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Goal { get; set; }
    public string? SuccessCriteria { get; set; }
    public PhaseStatus Status { get; set; }
    public int PhaseNumber { get; set; }
    public int OrderIndex { get; set; }
    public string? DependsOnPhaseIds { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Milestone Milestone { get; set; } = null!;
    public ICollection<LifecycleTask> Tasks { get; set; } = new List<LifecycleTask>();
}
