using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

/// <summary>A single turn in a multi-turn Q&amp;A conversation.</summary>
/// <param name="Role">"user" | "assistant"</param>
/// <param name="Content">The text of the turn.</param>
public record QaTurn(string Role, string Content);

/// <summary>Request body for POST /api/v1/ai/ask.</summary>
public class AskRequest
{
    /// <summary>The current user question (plain English).</summary>
    [Required]
    [MaxLength(4000)]
    public string Question { get; set; } = string.Empty;

    /// <summary>Prior turns in the conversation, oldest first (optional).</summary>
    public List<QaTurn>? History { get; set; }
}

/// <summary>Response from POST /api/v1/ai/ask.</summary>
/// <param name="Answer">The assistant's plain-English answer.</param>
/// <param name="ToolsUsed">Distinct list of tool names the model invoked to answer.</param>
/// <param name="LlmAvailable">False when no API key is configured (noop path).</param>
/// <param name="TokensUsed">Total input + output tokens consumed across all loop iterations.</param>
/// <param name="ModelId">The model that produced the answer.</param>
public record AskResponse(
    string Answer,
    List<string> ToolsUsed,
    bool LlmAvailable,
    int TokensUsed,
    string ModelId);
