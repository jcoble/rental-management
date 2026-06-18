using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Models.Accounting;

// ---------------------------------------------------------------------------
// Provider-agnostic transport types for the accounting-integration backbone.
//
// These are the contract surface every IAccountingProvider speaks. They carry
// NO provider-specific shape — a QuickBooks provider, a Xero provider, etc. all
// project their native payloads into these neutral records, so the connection
// service / workers / import engine never see an Intuit-flavoured type.
//
// Modeled on EdiPlatform's Models/Erp/* (ErpPullResult<T>, ErpCustomerDto,
// ErpPaymentDto, OAuthCallbackResult) re-skinned to the pull-first, direction-
// agnostic surface in the plan (§2.1).
// ---------------------------------------------------------------------------

/// <summary>
/// Per-call execution context handed to a provider for any authenticated call.
/// Holds the already-decrypted access token + the external company scope so the
/// provider never reaches into the persisted (encrypted) connection itself.
/// </summary>
/// <param name="Realm">Provider company/account scope — QuickBooks <c>realmId</c>, a Xero tenant id, etc.</param>
/// <param name="AccessToken">The decrypted OAuth access token for this call.</param>
/// <param name="UseSandbox">Route to the provider's sandbox base when true.</param>
public sealed record AcctCallCtx(string Realm, string AccessToken, bool UseSandbox);

/// <summary>
/// Provider-neutral OAuth app settings the connection service resolves from the
/// matching <c>*Options</c> POCO and hands to a provider. Keeps the backbone from
/// reading any provider-specific options type directly (e.g. <c>QuickBooksOptions</c>).
/// </summary>
/// <param name="Provider">Which provider these settings belong to.</param>
/// <param name="ClientId">OAuth app client id (from server secrets).</param>
/// <param name="ClientSecret">OAuth app client secret (from server secrets).</param>
/// <param name="UseSandbox">Whether the provider should use its sandbox endpoints.</param>
/// <param name="RedirectUri">
/// The default redirect URI registered with the provider's app. May be overridden per
/// request when the caller supplies one (the value sent must byte-match the app config).
/// </param>
public sealed record AccountingAppSettings(
    AccountingProvider Provider,
    string? ClientId,
    string? ClientSecret,
    bool UseSandbox,
    string? RedirectUri)
{
    /// <summary>True once both client id and secret are present — the fail-closed configured gate (AC-8).</summary>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// The query-string payload a provider receives at the OAuth callback. Generic
/// across providers; <see cref="Realm"/> carries QuickBooks' <c>realmId</c> (Xero
/// supplies its tenant id out-of-band, so it stays null there).
/// </summary>
/// <param name="Code">The authorization code to exchange for tokens.</param>
/// <param name="State">The CSRF state token issued when the authorize URL was built.</param>
/// <param name="Realm">QuickBooks <c>realmId</c> from the callback; null for providers that don't send one.</param>
/// <param name="Error">A provider error code present when the landlord denied/aborted consent.</param>
public sealed record AccountingCallback(
    string Code,
    string State,
    string? Realm,
    string? Error);

/// <summary>
/// Result of a token exchange or refresh. Both tokens rotate on QuickBooks (and
/// most OAuth2 providers), so the caller writes both back atomically.
/// </summary>
public sealed record AccountingTokenResult(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAtUtc,
    string? ExternalAccountId,
    string? CompanyName);

/// <summary>
/// Generic envelope for a single pull cycle of one resource — the port of
/// EdiPlatform's <c>ErpPullResult&lt;T&gt;</c>. The provider reports the high-water
/// mark so the worker can persist a delta cursor on the connection.
/// </summary>
/// <typeparam name="T">DTO type — <see cref="ExtCustomerDto"/>, <see cref="ExtPaymentDto"/>, etc.</typeparam>
public sealed record AccountingPullResult<T>(
    IReadOnlyList<T> Items,
    DateTime? MaxUpdatedAtUtc,
    bool MoreAvailable);

/// <summary>
/// Per-resource pull/push capability matrix a provider advertises. Drives both
/// the orchestration (which resources to pull/push) and the UI (what to offer),
/// so the backbone branches on capabilities — never on the provider name (AC-1).
/// </summary>
public sealed record AccountingCapabilities(
    bool CanPullCustomers,
    bool CanPullVendors,
    bool CanPullAccounts,
    bool CanPullPayments,
    bool CanPullExpenses,
    bool CanPushIncome,
    bool CanPushExpense)
{
    /// <summary>True when the provider can pull at least one resource (the primary direction).</summary>
    public bool SupportsPull =>
        CanPullCustomers || CanPullVendors || CanPullAccounts || CanPullPayments || CanPullExpenses;

    /// <summary>True when the provider can push at least one document kind (the secondary direction).</summary>
    public bool SupportsPush => CanPushIncome || CanPushExpense;
}

// --- Pull DTOs (accounting → Rental Command) ------------------------------------

/// <summary>An external accounting customer (maps → RC Tenant/Lease). Port of <c>ErpCustomerDto</c>.</summary>
public sealed record ExtCustomerDto(
    string ExternalId,
    string DisplayName,
    bool IsActive,
    DateTime? UpdatedAtUtc,
    string? Email,
    string? Phone,
    /// <summary>Free-form provider payload (addresses, custom fields) for later enrichment.</summary>
    string? MetadataJson);

/// <summary>An external accounting vendor (maps → RC Vendor).</summary>
public sealed record ExtVendorDto(
    string ExternalId,
    string DisplayName,
    bool IsActive,
    DateTime? UpdatedAtUtc,
    string? Email,
    string? Phone,
    string? TaxId,
    string? MetadataJson);

/// <summary>
/// An external account or class (maps → RC Property via Class, and Schedule-E
/// category via Account). <see cref="Kind"/> distinguishes the two QBO list types.
/// </summary>
public sealed record ExtAccountDto(
    string ExternalId,
    string Name,
    AccountingListKind Kind,
    bool IsActive,
    DateTime? UpdatedAtUtc,
    /// <summary>Provider account classification (e.g. QBO <c>AccountType</c>); null for classes.</summary>
    string? AccountType,
    string? MetadataJson);

/// <summary>Whether an <see cref="ExtAccountDto"/> came from the provider's Account list or its Class list.</summary>
public enum AccountingListKind
{
    Account,
    Class
}

/// <summary>An external money-in transaction (maps → RC <c>Payment</c>). Port of <c>ErpPaymentDto</c>.</summary>
/// <param name="DepositAccountExternalId">
/// Neutral external id of the account the money was deposited to (QBO <c>DepositToAccountRef.value</c>,
/// a Xero bank-account id, etc.). The provider projects it so the generic import service can recognise a
/// deposit/liability account WITHOUT parsing provider-shaped JSON. Null when the provider doesn't supply one.
/// </param>
public sealed record ExtPaymentDto(
    string ExternalId,
    string? CustomerExternalId,
    decimal Amount,
    DateTime TxnDateUtc,
    string? PaymentMethod,
    string? ReferenceNumber,
    DateTime? UpdatedAtUtc,
    /// <summary>External invoice ids this payment applies to (QBO linked txns).</summary>
    IReadOnlyList<string>? InvoiceExternalIds,
    string? DepositAccountExternalId,
    string? MetadataJson);

/// <summary>An external money-out transaction (maps → RC <c>Expense</c>).</summary>
/// <param name="SourceKind">
/// Neutral discriminator for the external object kind the PROVIDER pulled this from — the provider
/// knows whether it was a QBO Purchase vs Bill (or a Xero Bank Transaction vs Bill), so the generic
/// import service reads this instead of sniffing provider-shaped JSON. Use the <c>ExternalKind</c>
/// vocabulary values ("Purchase" | "Bill").
/// </param>
public sealed record ExtExpenseDto(
    string ExternalId,
    string? VendorExternalId,
    string? AccountExternalId,
    string? ClassExternalId,
    decimal Amount,
    DateTime TxnDateUtc,
    string? ReferenceNumber,
    DateTime? UpdatedAtUtc,
    string SourceKind,
    string? MetadataJson);

// --- Push docs (Rental Command → accounting) ------------------------------------

/// <summary>
/// A resolved income document to push (RC Payment → SalesReceipt/Invoice). The
/// caller passes a stable <see cref="ExternalDocNumber"/> so the provider can be
/// idempotent (query-by-doc-number before create).
/// </summary>
public sealed record AcctIncomeDoc(
    string ExternalDocNumber,
    string? CustomerExternalId,
    decimal Amount,
    DateTime TxnDateUtc,
    string? PaymentMethod,
    string? Memo);

/// <summary>
/// A resolved expense document to push (RC Expense → Purchase/Bill). Idempotent
/// via <see cref="ExternalDocNumber"/> (carried as a doc number / private note).
/// </summary>
public sealed record AcctExpenseDoc(
    string ExternalDocNumber,
    string? VendorExternalId,
    string? AccountExternalId,
    string? ClassExternalId,
    decimal Amount,
    DateTime TxnDateUtc,
    string? Memo);

/// <summary>The outcome of an idempotent push create/update.</summary>
public enum AcctPushOutcome
{
    Created,
    AlreadyExisted,
    Updated
}

/// <summary>Result of an income/expense push — the outcome plus the provider-side object id.</summary>
public sealed record AcctPushResult(AcctPushOutcome Outcome, string? ExternalId);
