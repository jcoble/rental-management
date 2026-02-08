namespace Lifecycle.Data.Entities;

public class TaskLabel
{
    public int TaskId { get; set; }
    public int LabelId { get; set; }
    public LifecycleTask Task { get; set; } = null!;
    public Label Label { get; set; } = null!;
}
