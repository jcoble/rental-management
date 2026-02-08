using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class Project
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Repository { get; set; }
    public ProjectStatus Status { get; set; }
    public string? Settings { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<Milestone> Milestones { get; set; } = new List<Milestone>();
    public ICollection<Label> Labels { get; set; } = new List<Label>();
    public ICollection<ActivityLog> Activities { get; set; } = new List<ActivityLog>();
}
