using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class TestStepResult
{
    public int Id { get; set; }
    public int TestExecutionId { get; set; }
    public int TestStepId { get; set; }
    public TestStepStatus Status { get; set; }
    public string? ActualResult { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Screenshot { get; set; }
    public int DurationMs { get; set; }
    public DateTime ExecutedAt { get; set; }
    public TestExecution Execution { get; set; } = null!;
    public TestStep Step { get; set; } = null!;
}
