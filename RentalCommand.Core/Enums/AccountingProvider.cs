namespace RentalCommand.Core.Enums;

/// <summary>
/// The external accounting systems Rental Command can connect to. The backbone
/// (connection lifecycle, OAuth, workers, ledger, mapping, settings shell) is
/// provider-agnostic — it only ever discriminates on this enum, never on a
/// provider-specific type. Adding a provider = a new <c>IAccountingProvider</c>
/// implementation + a value here + a registration line; nothing else changes.
/// </summary>
public enum AccountingProvider
{
    /// <summary>QuickBooks Online (provider #1). Intuit OAuth2, realmId-scoped, no PKCE.</summary>
    QuickBooks,
    // Xero, FreshBooks, Wave … land as future drop-ins behind IAccountingProvider.
}
