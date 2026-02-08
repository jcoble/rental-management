namespace Lifecycle.Data.Entities;

public class Label
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public required string Name { get; set; }
    public required string Color { get; set; }
    public string? Description { get; set; }
    public Project Project { get; set; } = null!;
    public ICollection<TaskLabel> TaskLabels { get; set; } = new List<TaskLabel>();
}
