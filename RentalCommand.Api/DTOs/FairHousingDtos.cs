using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

/// <summary>Request body for the Fair Housing copy check: the landlord-written text to review.</summary>
public class FairHousingCheckRequest
{
    [Required]
    [MaxLength(8000)]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Result of a Fair Housing Act review of a piece of copy.
/// </summary>
public class FairHousingReviewResult
{
    /// <summary>
    /// True only when an LLM actually reviewed the text. When false (no API key configured or the
    /// provider returned nothing usable), the UI must say the review is unavailable and must NEVER
    /// imply the copy is compliant.
    /// </summary>
    public bool Reviewed { get; set; }

    /// <summary>
    /// True when the reviewer found no Fair-Housing concerns. Only meaningful when
    /// <see cref="Reviewed"/> is true.
    /// </summary>
    public bool Compliant { get; set; }

    /// <summary>Specific phrases flagged as discriminatory or risky, each with the concern.</summary>
    public IReadOnlyList<FairHousingIssue> Issues { get; set; } = [];

    /// <summary>A compliant rewrite of the full text, when issues were found and one could be produced.</summary>
    public string? SuggestedRewrite { get; set; }
}

/// <summary>A single flagged phrase and the Fair-Housing concern it raises.</summary>
public class FairHousingIssue
{
    /// <summary>The exact (or close) phrase from the copy that raised the concern.</summary>
    public string Phrase { get; set; } = string.Empty;

    /// <summary>Plain-language explanation of why the phrase is a Fair-Housing risk.</summary>
    public string Concern { get; set; } = string.Empty;
}
