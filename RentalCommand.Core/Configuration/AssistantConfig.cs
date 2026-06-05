namespace RentalCommand.Core.Configuration;

/// <summary>
/// Configuration for the LLM assistant / scan-extraction provider. Bound from the
/// "Assistant" configuration section.
/// </summary>
public class AssistantConfig
{
    public const string SectionName = "Assistant";

    /// <summary>LLM provider discriminator: "openai" (default), "anthropic", or "gemini".</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Model identifier (e.g. "gpt-4o", "claude-3-5-sonnet-latest", "gemini-2.5-flash").</summary>
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

    /// <summary>
    /// When <c>true</c>, attempts to OCR image documents locally via the <c>tesseract</c> CLI
    /// (must be on PATH) and sends the extracted text to the LLM instead of the vision path.
    /// Falls back to vision automatically if the binary is absent, returns an error, or yields
    /// fewer than ~20 non-whitespace characters. Defaults to <c>false</c>.
    /// Requires: <c>tesseract</c> binary on PATH (e.g. <c>brew install tesseract</c> / apt tesseract-ocr).
    /// </summary>
    public bool UseImageOcr { get; set; } = false;
}
