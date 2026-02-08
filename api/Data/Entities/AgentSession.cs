namespace Lifecycle.Data.Entities;

public class AgentSession
{
    public int Id { get; set; }
    public int TeamMemberId { get; set; }
    public required string SessionId { get; set; }
    public required string Status { get; set; } // Active, Completed, Failed, Terminated
    public DateTime SpawnedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? TokensUsed { get; set; }
    public string? CurrentActivity { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public TeamMember TeamMember { get; set; } = null!;
}
