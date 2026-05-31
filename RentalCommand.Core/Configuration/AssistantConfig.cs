namespace RentalCommand.Core.Configuration;

/// <summary>
/// Configuration for the LLM assistant / scan-extraction provider. Bound from the
/// "Assistant" configuration section.
/// </summary>
public class AssistantConfig
{
    public const string SectionName = "Assistant";

    /// <summary>LLM provider discriminator: "openai" (default) or "anthropic".</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Model identifier (e.g. "gpt-4o").</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Provider API key (resolved from secrets/env in non-dev environments).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Maximum context length (tokens) the model supports.</summary>
    public int ContextLength { get; set; }

    /// <summary>
    /// OpenAI vision image detail level: "low", "high", or "auto". "low" uses a fixed
    /// 85-token budget per image (cheapest); "high" enables tiled analysis; "auto" lets
    /// the model decide. Only used by <see cref="OpenAiLlmProvider"/>; ignored by Anthropic.
    /// </summary>
    public string ImageDetail { get; set; } = "low";
}
