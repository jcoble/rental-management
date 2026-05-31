namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Abstraction over an LLM provider. Phase 0 defines the contract only; a concrete
/// Anthropic/OpenAI implementation lands in Phase 2/3.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Single-shot chat completion.</summary>
    Task<string> ChatAsync(string prompt, CancellationToken ct = default);

    /// <summary>
    /// Extract structured fields from a document image or PDF (the flagship scan-&gt;LLM intake flow).
    /// </summary>
    Task<ExtractedFields> ExtractAsync(byte[] imageOrPdfBytes, string prompt, CancellationToken ct = default);
}

/// <summary>Result of an <see cref="ILlmProvider.ExtractAsync"/> call.</summary>
public class ExtractedFields
{
    /// <summary>Extracted fields keyed by field name.</summary>
    public Dictionary<string, FieldExtraction> Fields { get; set; } = new();

    /// <summary>Identifier of the model that produced the extraction.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Total tokens consumed by the extraction call.</summary>
    public int TokensUsed { get; set; }
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
