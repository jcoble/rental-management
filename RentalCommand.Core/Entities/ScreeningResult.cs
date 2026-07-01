using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// The outcome of a background/credit screening run against a submitted <see cref="RentalApplication"/>.
/// Screening is only ever requested for an application that recorded explicit FCRA consent
/// (<see cref="RentalApplication.ConsentGiven"/>). The screening PROVIDER is gated like Stripe/e-sign:
/// when no API key is configured the request is recorded as <see cref="ScreeningStatus.Failed"/> with a
/// clear "not configured" note — never a false "passed".
/// </summary>
public class ScreeningResult
{
    public int Id { get; set; }

    /// <summary>Owning portfolio (scoped from the caller's JWT; never trusted from the client).</summary>
    public int PortfolioId { get; set; }

    /// <summary>The application this screening was run for.</summary>
    public int ApplicationId { get; set; }

    public ScreeningStatus Status { get; set; } = ScreeningStatus.Requested;

    /// <summary>Coarse credit band the provider reported, e.g. "Excellent"/"Good"/"Fair"/"Poor". Null until completed.</summary>
    public string? CreditScoreBand { get; set; }

    /// <summary>Whether the criminal-records check found a record. Null when unknown/not run.</summary>
    public bool? HasCriminalRecord { get; set; }

    /// <summary>Whether the eviction-history check found a record. Null when unknown/not run.</summary>
    public bool? HasEvictionRecord { get; set; }

    /// <summary>The provider's (or our) overall recommendation. Null until completed.</summary>
    public ScreeningRecommendation? Recommendation { get; set; }

    /// <summary>Provider-side identifier for the screening report (so a result can be re-fetched).</summary>
    public string? ProviderReference { get; set; }

    /// <summary>Raw provider response (Postgres jsonb) for audit/troubleshooting. Never shown raw to the landlord.</summary>
    public string? RawResultJson { get; set; }

    public DateTime RequestedAtUtc { get; set; }

    /// <summary>When the provider returned a terminal result (completed or failed); null while pending.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public RentalApplication? Application { get; set; }
}
