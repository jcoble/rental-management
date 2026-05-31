namespace RentalCommand.Core.Configuration;

/// <summary>
/// Configuration for the X-API-Key authentication scheme used by webhooks / external integrations.
/// Each entry stores the key's prefix (first 8 chars) plus the SHA-256 hash (Base64) of the full key,
/// so raw keys are never persisted. Phase 0 sources keys from configuration; a later phase may move
/// them to a dedicated ApiKey table without changing the handler contract.
/// </summary>
public class ApiKeySettings
{
    public const string SectionName = "ApiKeys";

    public List<ApiKeyEntry> Keys { get; set; } = new();
}

public class ApiKeyEntry
{
    /// <summary>First 8 characters of the API key (fast lookup discriminator).</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Base64 SHA-256 hash of the full API key.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Friendly name for the key (used as the principal name / claim).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional portfolio this key is scoped to.</summary>
    public int? PortfolioId { get; set; }
}
