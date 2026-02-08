using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class TestExecution
{
    public int Id { get; set; }
    public int TestPlanId { get; set; }
    public TestExecutionMode ExecutionMode { get; set; }
    public TestExecutionStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int TotalSteps { get; set; }
    public int PassedSteps { get; set; }
    public int FailedSteps { get; set; }
    public int SkippedSteps { get; set; }
    public string? FailureReason { get; set; }
    public string? ExecutedBy { get; set; }
    public TestPlan TestPlan { get; set; } = null!;
    public ICollection<TestStepResult> StepResults { get; set; } = new List<TestStepResult>();
}
