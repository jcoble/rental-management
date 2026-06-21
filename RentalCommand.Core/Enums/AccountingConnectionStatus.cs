namespace RentalCommand.Core.Enums;

/// <summary>
/// Lifecycle state of an <see cref="Entities.AccountingConnection"/>
/// (ported verbatim from EdiPlatform's <c>ErpConnectionStatus</c>).
/// </summary>
public enum AccountingConnectionStatus
{
    /// <summary>OAuth flow started, tokens not yet exchanged.</summary>
    Pending,

    /// <summary>Tokens valid; pull/push may run.</summary>
    Connected,

    /// <summary>Refresh token expired or revoked; the landlord must reconnect.</summary>
    NeedsReconnect,

    /// <summary>Last sync attempt threw an error; will retry.</summary>
    Error,

    /// <summary>The landlord disconnected via the UI.</summary>
    Disconnected
}
