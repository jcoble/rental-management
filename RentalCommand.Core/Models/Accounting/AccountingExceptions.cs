namespace RentalCommand.Core.Models.Accounting;

/// <summary>
/// Thrown by a provider when the OAuth refresh token is permanently dead (revoked / expired —
/// QuickBooks' <c>invalid_grant</c>, the equivalent on other providers). Provider-neutral so the
/// token-refresh worker and the inline 401-retry can flip the connection to
/// <c>NeedsReconnect</c> WITHOUT string-matching a provider-specific error message (AC-1, AC-7).
/// A reconnect is the only remedy — re-trying the refresh will not help.
/// </summary>
public sealed class AccountingReconnectRequiredException : Exception
{
    public AccountingReconnectRequiredException(string message) : base(message) { }

    public AccountingReconnectRequiredException(string message, Exception inner) : base(message, inner) { }
}
