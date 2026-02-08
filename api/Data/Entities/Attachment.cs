namespace Lifecycle.Data.Entities;

public class Attachment
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public required string FileName { get; set; }
    public required string OriginalFileName { get; set; }
    public required string ContentType { get; set; }
    public long FileSize { get; set; }
    public required string StoragePath { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
    public LifecycleTask Task { get; set; } = null!;
}
