namespace RentalCommand.Core.Configuration;

/// <summary>
/// Configuration for the electronic-signature provider (e.g. Dropbox Sign / HelloSign). Bound from the
/// "Esign" configuration section. Like Stripe and the LLM provider, the integration is gated: when no
/// API key is configured the workflow is wired and testable but the real third-party call never fires.
/// </summary>
public class EsignConfig
{
    public const string SectionName = "Esign";

    /// <summary>Provider discriminator, e.g. "DropboxSign". Informational; the gated provider is chosen at startup.</summary>
    public string Provider { get; set; } = "DropboxSign";

    /// <summary>Provider API key (resolved from secrets/env in non-dev environments).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Shared secret used to verify inbound provider webhook signatures.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// True when an API key has been configured. Gating flag — when false the e-sign workflow returns a
    /// clear "not configured" result and never contacts the provider.
    /// </summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}
