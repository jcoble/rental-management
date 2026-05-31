namespace RentalCommand.Core.Configuration;

/// <summary>
/// Configuration for the LLM assistant / scan-extraction provider. Bound from the
/// "Assistant" configuration section.
/// </summary>
public class AssistantConfig
{
    public const string SectionName = "Assistant";

    /// <summary>Model identifier (e.g. "claude-opus-4-8").</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Provider API key (resolved from secrets/env in non-dev environments).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Maximum context length (tokens) the model supports.</summary>
    public int ContextLength { get; set; }
}
