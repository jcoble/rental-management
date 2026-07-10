namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Resolves and sends an SMS for a portfolio: the portfolio's own configured provider takes
/// precedence; when it has none configured the platform-level env credentials are used as the
/// default/fallback. Fail-soft — a missing/misconfigured provider is logged (suppressed), never
/// thrown, so the outbox dispatch worker is never crashed by SMS config state. A real transport
/// error from a configured provider DOES throw so the worker can retry.
/// </summary>
public interface ISmsDispatcher : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    /// <summary>
    /// Sends <paramref name="message"/> to <paramref name="toPhoneNumber"/> on behalf of
    /// <paramref name="portfolioId"/>. <paramref name="portfolioId"/> is null for platform-level
    /// sends (e.g. test-from-platform), which use the env credentials only.
    /// </summary>
    Task SendAsync(int? portfolioId, string toPhoneNumber, string message, CancellationToken ct = default);
}
