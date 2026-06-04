namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Account-wide sandbox check. A portfolio in Sandbox is a demo account seeded with fake data; while
/// sandboxed, all real outbound (email/SMS/Stripe/e-sign) must be HARD-suppressed so demo play never
/// contacts real people or moves real money. Implementations resolve <c>Portfolio.IsSandbox</c> for the
/// given portfolio id (treating an unknown/unscoped portfolio as NOT sandbox — fail-open to Live so a
/// legitimate system message is never silently dropped).
/// </summary>
public interface ISandboxGuard
{
    /// <summary>
    /// True when the portfolio is currently in Sandbox mode. A null/zero portfolio id (system/auth
    /// messages with no portfolio scope) returns false — those are real, never suppressed.
    /// </summary>
    Task<bool> IsSandboxAsync(int? portfolioId, CancellationToken ct = default);
}
