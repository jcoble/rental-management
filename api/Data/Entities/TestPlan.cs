using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class TestPlan
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public TestLevel RequiredLevel { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public TestPlanStatus Status { get; set; }
    public TestPlanSource Source { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public LifecycleTask Task { get; set; } = null!;
    public ICollection<Test> Tests { get; set; } = new List<Test>();
    public ICollection<TestExecution> Executions { get; set; } = new List<TestExecution>();
}
