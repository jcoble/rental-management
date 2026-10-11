namespace RentalCommand.Core.Entities;

/// <summary>
/// The active, encrypted bring-your-own LLM credential for one workspace.
/// The cipher text is persistence-only and must never be projected by an API.
/// </summary>
public sealed class WorkspaceLlmCredential
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ApiKeyCipherText { get; set; } = string.Empty;
    public DateTime LastTestedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? RotatedAtUtc { get; set; }
    public Portfolio? Portfolio { get; set; }
}
