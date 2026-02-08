using Lifecycle.Data.Enums;
using TaskStatus = Lifecycle.Data.Enums.TaskStatus;

namespace Lifecycle.Data.Entities;

public class LifecycleTask
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int? PhaseId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public TaskStatus Status { get; set; }
    public TaskPriority Priority { get; set; }
    public TaskType Type { get; set; }
    public TaskSource Source { get; set; }
    public int OrderInColumn { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? GitCommitSha { get; set; }
    public string? GitBranch { get; set; }
    public string? PullRequestUrl { get; set; }
    public string? ConversationRef { get; set; }
    public TestLevel? RequiredTestLevel { get; set; }
    public TestAutonomyLevel? TestAutonomyLevel { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Project Project { get; set; } = null!;
    public Phase? Phase { get; set; }
    public ICollection<TestPlan> TestPlans { get; set; } = new List<TestPlan>();
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
    public ICollection<TaskLabel> TaskLabels { get; set; } = new List<TaskLabel>();
    public ICollection<TaskAssignment> Assignments { get; set; } = new List<TaskAssignment>();
}
