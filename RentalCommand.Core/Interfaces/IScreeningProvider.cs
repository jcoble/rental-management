namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Abstraction over a tenant-screening provider (e.g. TransUnion). Phase 0 defines the
/// contract only; a concrete integration lands in a later phase.
/// </summary>
public interface IScreeningProvider
{
    /// <summary>Request a background/credit screening for an applicant and return the result.</summary>
    Task<ScreeningResult> RequestScreeningAsync(
        ScreeningRequest request,
        CancellationToken ct = default);
}

/// <summary>Applicant details submitted for screening.</summary>
public class ScreeningRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Ssn { get; set; }
    public string? Address { get; set; }
}

/// <summary>Outcome of a screening request.</summary>
public class ScreeningResult
{
    /// <summary>Provider-side identifier for the screening report.</summary>
    public string? ReportId { get; set; }

    /// <summary>Provider status (e.g. "Pending", "Completed").</summary>
    public string Status { get; set; } = string.Empty;

    public int? CreditScore { get; set; }

    /// <summary>Provider-hosted URL of the full report, if available.</summary>
    public string? ReportUrl { get; set; }
}
