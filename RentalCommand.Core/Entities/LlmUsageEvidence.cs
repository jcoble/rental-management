namespace RentalCommand.Core.Entities;

/// <summary>
/// Secret-free evidence for one workspace-funded LLM invocation.
/// Raw prompts, documents, responses, and credentials are deliberately absent.
/// </summary>
public sealed class LlmUsageEvidence
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string Feature { get; set; } = string.Empty;
    public int LatencyMilliseconds { get; set; }
    public int InputUnits { get; set; }
    public int OutputUnits { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public Portfolio? Portfolio { get; set; }
}
