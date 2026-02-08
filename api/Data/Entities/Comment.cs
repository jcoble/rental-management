using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class Comment
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public required string Content { get; set; }
    public CommentSource Source { get; set; }
    public string? Author { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public LifecycleTask Task { get; set; } = null!;
}
