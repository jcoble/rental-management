namespace RentalCommand.Core.Configuration;

/// <summary>
/// Configuration for the tenant-screening provider (e.g. TransUnion SmartMove). Bound from the
/// "Screening" configuration section. Like Stripe, the LLM provider, and e-sign, the integration is
/// gated: when no API key is configured the screening workflow is wired and testable but the real
/// third-party call never fires — and the gated provider returns a clear "not configured" result,
/// never a false "passed".
/// </summary>
public class ScreeningConfig
{
    public const string SectionName = "Screening";

    /// <summary>Provider discriminator, e.g. "TransUnion". Informational; the gated provider is chosen at startup.</summary>
    public string Provider { get; set; } = "TransUnion";

    /// <summary>Base URL of the provider's screening API. Defaults to TransUnion SmartMove's partner endpoint.</summary>
    public string BaseUrl { get; set; } = "https://api.mysmartmove.com/";

    /// <summary>Provider API key / partner credential (resolved from secrets/env in non-dev environments).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional provider account / partner identifier sent alongside the API key.</summary>
    public string? AccountId { get; set; }

    // --- Credit-reporting agency (CRA) identity for the FCRA adverse-action notice ---
    // Defaults describe TransUnion, the CRA behind SmartMove. The adverse-action notice must name the
    // CRA, give its address/phone, and state that the CRA did not make the decision.

    /// <summary>Name of the credit-reporting agency that supplied the report (printed on the FCRA notice).</summary>
    public string CreditReportingAgencyName { get; set; } = "TransUnion Consumer Solutions";

    /// <summary>Mailing address of the CRA (printed on the FCRA notice so the applicant can request their report).</summary>
    public string CreditReportingAgencyAddress { get; set; } = "P.O. Box 2000, Chester, PA 19016-2000";

    /// <summary>Toll-free phone of the CRA (printed on the FCRA notice).</summary>
    public string CreditReportingAgencyPhone { get; set; } = "1-800-916-8800";

    /// <summary>
    /// True when an API key has been configured. Gating flag — when false the screening workflow
    /// returns a clear "not configured" result and never contacts the provider.
    /// </summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}
