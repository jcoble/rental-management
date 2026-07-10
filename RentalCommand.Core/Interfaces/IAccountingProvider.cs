using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// The provider-agnostic accounting-integration seam. One implementation per
/// external accounting system (QuickBooks is provider #1; Xero/FreshBooks/Wave
/// are future drop-ins). Direction-agnostic: every provider can pull and/or push,
/// and advertises exactly what it supports via <see cref="Capabilities"/>; the
/// connection service, workers, import/push engines, and settings shell only ever
/// go through this interface + <see cref="AccountingProvider"/> + the
/// capability matrix — never a provider-specific type (AC-1).
///
/// <para>
/// Ported from EdiPlatform's <c>IErpProvider</c>, re-skinned to the pull-first
/// surface in the plan (§2.1): the provider receives an already-decrypted
/// <see cref="AcctCallCtx"/> and provider-neutral <see cref="AccountingAppSettings"/>
/// rather than reaching into the persisted connection or a credential store.
/// </para>
/// </summary>
public interface IAccountingProvider : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    /// <summary>Which provider this implementation handles (the resolver key).</summary>
    AccountingProvider Provider { get; }

    /// <summary>Per-resource pull/push capabilities — drives the UI and the orchestration.</summary>
    AccountingCapabilities Capabilities { get; }

    // --- OAuth (PKCE optional; QuickBooks = no PKCE, so codeChallenge is null) ---

    /// <summary>
    /// Build the provider's authorize URL. The caller has already persisted an
    /// <c>OAuthState</c> row carrying <paramref name="state"/>; this only formats the URL.
    /// </summary>
    string BuildAuthorizeUrl(
        AccountingAppSettings settings,
        string redirectUri,
        string state,
        string? codeChallenge);

    /// <summary>
    /// Exchange the authorization code for tokens. The caller persists the result
    /// onto the connection (encrypted); the implementation performs only the HTTP
    /// exchange + parsing.
    /// </summary>
    Task<AccountingTokenResult> ExchangeCodeAsync(
        AccountingAppSettings settings,
        AccountingCallback callback,
        CancellationToken cancellationToken);

    /// <summary>
    /// Refresh the access token. Returns the new pair (refresh tokens rotate on
    /// QuickBooks; both are written back atomically by the caller).
    /// </summary>
    Task<AccountingTokenResult> RefreshTokenAsync(
        AccountingAppSettings settings,
        string refreshToken,
        CancellationToken cancellationToken);

    /// <summary>Best-effort revoke at the provider. Failures are logged, not raised — local Disconnected is what matters.</summary>
    Task RevokeAsync(
        AccountingAppSettings settings,
        string refreshToken,
        CancellationToken cancellationToken);

    // --- PULL (primary direction) — generic DTOs, paged, delta via `since` ---

    /// <summary>Pull customers updated since <paramref name="since"/> (null = full pull). Implementations page until exhausted or set <c>MoreAvailable</c>.</summary>
    Task<AccountingPullResult<ExtCustomerDto>> PullCustomersAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken);

    /// <summary>Pull vendors updated since <paramref name="since"/>.</summary>
    Task<AccountingPullResult<ExtVendorDto>> PullVendorsAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken);

    /// <summary>Pull accounts and classes updated since <paramref name="since"/>.</summary>
    Task<AccountingPullResult<ExtAccountDto>> PullAccountsAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken);

    /// <summary>Pull money-in transactions (payments) updated since <paramref name="since"/>.</summary>
    Task<AccountingPullResult<ExtPaymentDto>> PullPaymentsAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken);

    /// <summary>Pull money-out transactions (Purchase/Bill) updated since <paramref name="since"/>.</summary>
    Task<AccountingPullResult<ExtExpenseDto>> PullExpensesAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken);

    // --- PUSH (secondary) — caller passes a stable external key; provider is idempotent ---

    /// <summary>Push an income document (SalesReceipt/Invoice). MUST be idempotent on the doc number.</summary>
    Task<AcctPushResult> UpsertIncomeAsync(
        AcctCallCtx ctx, AcctIncomeDoc doc, CancellationToken cancellationToken);

    /// <summary>Push an expense document (Purchase/Bill). MUST be idempotent on the doc number.</summary>
    Task<AcctPushResult> UpsertExpenseAsync(
        AcctCallCtx ctx, AcctExpenseDoc doc, CancellationToken cancellationToken);
}
