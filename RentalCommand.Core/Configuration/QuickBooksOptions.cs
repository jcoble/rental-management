namespace RentalCommand.Core.Configuration;

/// <summary>
/// QuickBooks Online OAuth app credentials + environment. Mirrors
/// <see cref="StripeConfig"/> / <c>PlaidOptions</c>: a section name, nullable
/// secrets sourced from server config (user-secrets in dev, container env in
/// prod), and a <see cref="Configured"/> gate so QuickBooks features are simply
/// off when no creds are present (AC-8) rather than throwing.
///
/// <para>
/// This is the ONLY QuickBooks-specific type in the Phase 1 backbone. The
/// connection service never reads it directly — an
/// <c>AccountingAppSettingsResolver</c> maps the <c>AccountingProvider</c> enum to
/// this POCO and projects it into the provider-neutral
/// <c>AccountingAppSettings</c>, keeping the backbone provider-agnostic (AC-1).
/// </para>
/// </summary>
public class QuickBooksOptions
{
    public const string SectionName = "QuickBooks";

    /// <summary>Intuit app client id (Development vs Production keys per environment).</summary>
    public string? ClientId { get; set; }

    /// <summary>Intuit app client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary><c>"sandbox"</c> | <c>"production"</c>. Anything other than production routes to sandbox.</summary>
    public string Environment { get; set; } = "sandbox";

    /// <summary>
    /// The OAuth redirect URI. MUST byte-match a URI registered on the Intuit app,
    /// or Intuit rejects the authorize/exchange. Falls back to the request-derived
    /// callback URL when unset.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>True once a client id and secret are configured — the fail-closed gate.</summary>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>True unless <see cref="Environment"/> is explicitly "production".</summary>
    public bool UseSandbox =>
        !string.Equals(Environment, "production", StringComparison.OrdinalIgnoreCase);
}
