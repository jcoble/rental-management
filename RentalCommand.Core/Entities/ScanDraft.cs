namespace RentalCommand.Core.Entities;

/// <summary>
/// Draft produced by the scan → LLM intake pipeline (entity shape only in Phase 0;
/// extraction is Phase 2). <see cref="ExtractedFields"/> holds JSON of
/// {value, confidence, sourceBox} per field.
/// </summary>
public class ScanDraft
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>
    /// Owning bulk-scan batch, when this draft was created via a batch upload; null for single-file
    /// scans. Links the draft into a <see cref="ScanBatch"/> review queue.
    /// </summary>
    public int? BatchId { get; set; }

    public string FilePath { get; set; } = string.Empty;

    /// <summary>Storage key of a small downscaled JPEG preview; null until generated (or for
    /// non-image uploads like PDFs). Served to clients by default so phones never fetch the
    /// full-resolution original just to render the review thumbnail.</summary>
    public string? ThumbnailPath { get; set; }

    public string TargetEntityType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    /// <summary>JSON: per-field {value, confidence, sourceBox}.</summary>
    public string? ExtractedFields { get; set; }

    public string? ModelId { get; set; }
    public int? TokensUsed { get; set; }
    public decimal? CostUsd { get; set; }

    /// <summary>
    /// Concise, human-readable reason a draft ended in <c>Status == "Failed"</c> or was explicitly
    /// rejected. Surfaced on the review screen so the user knows why a scan could not be used or
    /// why they chose not to keep it.
    /// </summary>
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public ScanBatch? Batch { get; set; }
}
