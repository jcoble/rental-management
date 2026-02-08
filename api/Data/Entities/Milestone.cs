using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class Milestone
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public MilestoneStatus Status { get; set; }
    public int OrderIndex { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? TargetDate { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Project Project { get; set; } = null!;
    public ICollection<Phase> Phases { get; set; } = new List<Phase>();
}
