namespace Lifecycle.Data.Entities;

public class TaskAssignment
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int? TeamMemberId { get; set; }
    public required string AssignedBy { get; set; } // Manual, ProjectManager, Self-Claimed
    public required string Status { get; set; } // Assigned, Claimed, InProgress, Completed, Abandoned
    public DateTime AssignedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public LifecycleTask Task { get; set; } = null!;
    public TeamMember? TeamMember { get; set; }
}
