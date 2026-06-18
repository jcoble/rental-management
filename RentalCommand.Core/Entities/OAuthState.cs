using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Single-use CSRF token store for the OAuth authorize → callback round trip
/// (port of EdiPlatform's <c>OAuthState</c>). On callback we look up the row by
/// <see cref="StateToken"/>, verify expiry + portfolio ownership, then DELETE it
/// (single-use). A nightly sweep removes expired rows that were never consumed.
///
/// <para>Portfolio-scoped for RLS. Not <c>IAuditable</c> — these are ephemeral
/// security tokens, not business records, so they stay out of the audit trail.</para>
/// </summary>
public class OAuthState : IPortfolioScoped
{
    public int Id { get; set; }

    public int PortfolioId { get; set; }

    public AccountingProvider Provider { get; set; }

    /// <summary>Cryptographically random base64url opaque token echoed back as the OAuth <c>state</c>.</summary>
    public string StateToken { get; set; } = string.Empty;

    /// <summary>Bound to the request that issued the state — the callback must arrive at the same URI.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// PKCE code_verifier, set when the authorize URL was built and consumed at token
    /// exchange. QuickBooks does not use PKCE so this stays null for QuickBooks rows;
    /// the column is here so a PKCE provider (e.g. Xero) drops in with zero schema change.
    /// </summary>
    public string? CodeVerifier { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Portfolio? Portfolio { get; set; }
}
