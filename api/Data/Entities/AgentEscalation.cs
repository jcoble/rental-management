namespace Lifecycle.Data.Entities;

public class AgentEscalation
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int? TaskId { get; set; }
    public required string Description { get; set; }
    public required string Status { get; set; } // Pending, Resolved, Dismissed
    public string? Resolution { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Project Project { get; set; } = null!;
    public LifecycleTask? Task { get; set; }
}
