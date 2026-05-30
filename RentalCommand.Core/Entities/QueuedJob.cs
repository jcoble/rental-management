namespace RentalCommand.Core.Entities;

/// <summary>
/// Background work tracking. <see cref="Status"/> is one of
/// Pending / Running / Completed / Failed.
/// </summary>
public class QueuedJob
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";

    /// <summary>JSON payload for the job.</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }

    public Portfolio? Portfolio { get; set; }
}
