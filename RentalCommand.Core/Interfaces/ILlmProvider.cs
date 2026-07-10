namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Abstraction over an LLM provider. Phase 0 defines the contract only; a concrete
/// Anthropic/OpenAI implementation lands in Phase 2/3.
/// </summary>
public interface ILlmProvider : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    /// <summary>Single-shot chat completion.</summary>
    Task<string> ChatAsync(string prompt, CancellationToken ct = default);

    /// <summary>
    /// Extract structured fields from a document image or PDF (the flagship scan-&gt;LLM intake flow).
    /// </summary>
    Task<ExtractedFields> ExtractAsync(
        byte[] documentBytes,
        string contentType,                 // e.g. "image/jpeg", "application/pdf"
        string instructions,                // human-readable extraction instructions
        IReadOnlyList<ExtractionFieldSpec> fields,   // the schema the model must fill
        string? groundingContext = null,    // optional JSON of vendors/properties/units to match against
        CancellationToken ct = default);

    /// <summary>
    /// Multi-turn chat with tool-calling support. The model may call tools zero or more
    /// times; the caller handles each <see cref="LlmToolResult"/> with
    /// <c>StopReason == "tool_use"</c>, executes the tools, appends assistant + tool-result
    /// messages to the conversation, and calls this method again until
    /// <c>StopReason == "end"</c>.
    /// </summary>
    Task<LlmToolResult> ChatWithToolsAsync(
        string systemPrompt,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolSpec> tools,
        CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Tool-calling records
// ---------------------------------------------------------------------------

/// <summary>A tool the model may call, described by its name, description, and a JSON Schema string.</summary>
public sealed record LlmToolSpec(
    string Name,
    string Description,
    /// <summary>
    /// A JSON Schema object string, e.g.
    /// <c>{"type":"object","properties":{...},"required":[...]}</c>.
    /// </summary>
    string ParametersJsonSchema);

/// <summary>A single tool invocation requested by the model.</summary>
public sealed record LlmToolCall(
    string Id,
    string Name,
    /// <summary>Raw JSON string of the arguments object.</summary>
    string ArgumentsJson);

/// <summary>A single message in a multi-turn tool-calling conversation.</summary>
public sealed record LlmChatMessage(
    /// <summary>"user" | "assistant" | "tool"</summary>
    string Role,
    /// <summary>Text content for user/assistant turns, or the tool-result text for role="tool".</summary>
    string? Content = null,
    /// <summary>Present on an assistant turn that requested tool calls.</summary>
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    /// <summary>For role="tool": the <see cref="LlmToolCall.Id"/> this result answers.</summary>
    string? ToolCallId = null);

/// <summary>Result of a <see cref="ILlmProvider.ChatWithToolsAsync"/> call.</summary>
public sealed record LlmToolResult(
    /// <summary>"tool_use" when the model called tools; "end" for a final text answer; "error"/"noop" allowed.</summary>
    string StopReason,
    /// <summary>Final assistant text when <see cref="StopReason"/> is "end".</summary>
    string? Text,
    IReadOnlyList<LlmToolCall> ToolCalls,
    int InputTokens,
    int OutputTokens,
    string ModelId);

/// <summary>Result of an <see cref="ILlmProvider.ExtractAsync"/> call.</summary>
public class ExtractedFields
{
    /// <summary>Extracted fields keyed by field name.</summary>
    public Dictionary<string, FieldExtraction> Fields { get; set; } = new();

    /// <summary>Identifier of the model that produced the extraction.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Total tokens consumed by the extraction call (input + output, kept for back-compat).</summary>
    public int TokensUsed { get; set; }

    /// <summary>Input (prompt) tokens consumed by the extraction call.</summary>
    public int InputTokens { get; set; }

    /// <summary>Output (completion) tokens consumed by the extraction call.</summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// True when the provider's response was cut off by the output-token limit (OpenAI
    /// finish_reason="length", Anthropic stop_reason="max_tokens", Gemini finishReason="MAX_TOKENS").
    /// A truncated tool call usually yields no usable fields, so the caller should treat the
    /// extraction as failed rather than storing the partial/empty skeleton for review.
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Set when the provider could not produce a usable extraction (truncated, unparseable, or no
    /// model configured). A concise, human-readable reason the caller can surface to the reviewer
    /// and persist; null on a normal extraction (even one that happens to be empty).
    /// </summary>
    public string? FailureReason { get; set; }
}

/// <summary>A single extracted field value, its confidence, and where it was found in the source.</summary>
public class FieldExtraction
{
    /// <summary>The extracted value as text.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Confidence score in the range 0.0–1.0.</summary>
    public decimal Confidence { get; set; }

    /// <summary>Bounding box of the value within the source document, if known.</summary>
    public Box? SourceBox { get; set; }
}

/// <summary>Normalized bounding box (0.0–1.0 relative coordinates) within a source document page.</summary>
public class Box
{
    public int Page { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>One field the model is asked to extract, used to build the tool schema.</summary>
public sealed record ExtractionFieldSpec(
    string Name,            // JSON key, e.g. "vendor_name"
    string Type,            // "string" | "number" | "date" | "enum" | "array"
    string Description,     // guidance shown to the model
    bool Required = false,
    IReadOnlyList<string>? EnumValues = null,
    /// <summary>
    /// For <c>Type == "array"</c>: the object schema for each item in the array.
    /// Each element describes a property of the item object (Name = property key,
    /// Type = "string" | "number" | etc.). <see langword="null"/> for non-array fields.
    /// </summary>
    IReadOnlyList<ExtractionFieldSpec>? ItemFields = null);
