namespace RentalCommand.Api.Scanning;

/// <summary>
/// Small, provider-agnostic helpers for hardening LLM response parsing in the scan→draft pipeline.
/// Models occasionally wrap a tool-call's JSON arguments in a markdown code fence (```json … ```)
/// even when asked for raw JSON; <see cref="StripCodeFences"/> recovers the inner JSON so parsing
/// does not silently fall through to an empty extraction. <see cref="IsTruncatedFinishReason"/>
/// recognises the various "ran out of output tokens" signals across providers so a cut-off (and
/// therefore unusable) tool call is treated as a failure rather than a confident set of blanks.
/// </summary>
internal static class LlmResponseParsing
{
    /// <summary>
    /// Strips a leading/trailing markdown code fence (``` or ```json) from a JSON payload so it can
    /// be parsed. Returns the input unchanged when there is no fence. Safe on null/whitespace.
    /// </summary>
    public static string StripCodeFences(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;

        var s = raw.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;

        // Drop the opening fence line (``` optionally followed by a language tag like "json").
        var firstNewline = s.IndexOf('\n');
        if (firstNewline < 0) return s; // a lone "```" with no body — nothing to recover.
        s = s[(firstNewline + 1)..];

        // Drop the closing fence if present.
        var closing = s.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0) s = s[..closing];

        return s.Trim();
    }

    /// <summary>
    /// True when a provider's finish/stop reason indicates the response was cut off by the output
    /// token limit. Recognises OpenAI ("length"), Anthropic ("max_tokens") and Gemini ("MAX_TOKENS"),
    /// case-insensitively. A truncated structured-output call cannot be trusted to be complete.
    /// </summary>
    public static bool IsTruncatedFinishReason(string? finishReason) =>
        !string.IsNullOrWhiteSpace(finishReason)
        && (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase)
            || finishReason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase)
            || finishReason.Equals("MAX_TOKENS", StringComparison.OrdinalIgnoreCase));
}
