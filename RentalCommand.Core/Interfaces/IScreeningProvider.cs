using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Abstraction over a tenant-screening provider (e.g. TransUnion SmartMove). The integration is GATED
/// like Stripe, the LLM provider, and e-sign: when no API key is configured a disabled implementation
/// is used that reports itself unconfigured and returns a clear "not configured" result — never a false
/// "passed". Callers MUST have recorded explicit FCRA consent before requesting a screening.
/// </summary>
public interface IScreeningProvider : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    /// <summary>True when the provider is configured (an API key is present) and will make real calls.</summary>
    bool IsConfigured { get; }

    /// <summary>Request a background/credit screening for an applicant and return the result.</summary>
    Task<ScreeningProviderResult> RequestScreeningAsync(
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

    /// <summary>Our application id, echoed to the provider as a client reference for reconciliation.</summary>
    public int ApplicationId { get; set; }
}

/// <summary>
/// Outcome of a screening request from the provider. Distinct from the persisted
/// <see cref="Entities.ScreeningResult"/> entity — this is the transient provider response the
/// service maps onto a stored row.
/// </summary>
public class ScreeningProviderResult
{
    /// <summary>False when the provider is not configured; the operation was a no-op (never a false "passed").</summary>
    public bool IsConfigured { get; set; } = true;

    /// <summary>True when the provider returned a usable, completed report.</summary>
    public bool Completed { get; set; }

    /// <summary>Provider-side identifier for the screening report.</summary>
    public string? ProviderReference { get; set; }

    /// <summary>Coarse credit band, e.g. "Excellent"/"Good"/"Fair"/"Poor".</summary>
    public string? CreditScoreBand { get; set; }

    public bool? HasCriminalRecord { get; set; }
    public bool? HasEvictionRecord { get; set; }

    /// <summary>The provider's overall recommendation, mapped onto our vocabulary.</summary>
    public ScreeningRecommendation? Recommendation { get; set; }

    /// <summary>Raw provider response body for audit/troubleshooting.</summary>
    public string? RawResultJson { get; set; }

    /// <summary>Human-readable error/explanation when the screening could not be performed.</summary>
    public string? Error { get; set; }

    /// <summary>The provider is not configured — a clear no-op result, never a false "passed".</summary>
    public static ScreeningProviderResult NotConfigured() => new()
    {
        IsConfigured = false,
        Completed = false,
        Error = "Screening provider is not configured.",
    };
}
