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
    public string FilePath { get; set; } = string.Empty;
    public string TargetEntityType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    /// <summary>JSON: per-field {value, confidence, sourceBox}.</summary>
    public string? ExtractedFields { get; set; }

    public string? ModelId { get; set; }
    public int? TokensUsed { get; set; }
    public decimal? CostUsd { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
}
