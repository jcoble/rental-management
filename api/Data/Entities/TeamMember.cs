namespace Lifecycle.Data.Entities;

public class TeamMember
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public required string Role { get; set; }
    public required string AgentName { get; set; }
    public required string ModelName { get; set; }
    public bool IsPersistent { get; set; }
    public required string Status { get; set; } // Active, Idle, Suspended
    public string? ConfigJson { get; set; }
    public string? SpawnPromptTemplate { get; set; }
    public string? TriggerStatuses { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public Project Project { get; set; } = null!;
    public ICollection<AgentSession> Sessions { get; set; } = new List<AgentSession>();
    public ICollection<TaskAssignment> Assignments { get; set; } = new List<TaskAssignment>();
}
