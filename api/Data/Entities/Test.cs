using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class Test
{
    public int Id { get; set; }
    public int TestPlanId { get; set; }
    public int OrderIndex { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public TestType Type { get; set; }
    public TestStatus Status { get; set; }
    public string? TestFile { get; set; }
    public string? Framework { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunOutput { get; set; }
    public int TotalRuns { get; set; }
    public int PassedRuns { get; set; }
    public int FailedRuns { get; set; }
    public DateTime CreatedAt { get; set; }
    public TestPlan TestPlan { get; set; } = null!;
    public ICollection<TestStep> Steps { get; set; } = new List<TestStep>();
}
