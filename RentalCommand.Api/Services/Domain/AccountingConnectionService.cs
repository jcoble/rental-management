using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Thrown when a connect is attempted for a provider whose server credentials are
/// absent. The controller maps this to 422 so the flow fails closed (AC-8) rather
/// than throwing an opaque 500.
/// </summary>
public sealed class AccountingNotConfiguredException : Exception
{
    public AccountingNotConfiguredException(string message) : base(message) { }
}

/// <summary>
/// Connection-lifecycle service for the accounting-integration backbone — the
/// provider-agnostic port of EdiPlatform's <c>ErpConnectionService</c>. The
/// controller is thin and delegates everything here. Every provider interaction
/// goes through <see cref="AccountingProviderResolver"/> +
/// <see cref="AccountingAppSettingsResolver"/>, so nothing in this class names a
/// provider (AC-1).
///
/// <list type="bullet">
///   <item><see cref="StartConnectAsync"/>: persist a single-use <c>OAuthState</c> and return the provider authorize URL.</item>
///   <item><see cref="CompleteCallbackAsync"/>: validate-and-consume the state, exchange the code via the resolved provider, encrypt+persist tokens, flip Connected.</item>
///   <item><see cref="DisconnectAsync"/>: best-effort revoke, flip Disconnected, blank tokens.</item>
///   <item><see cref="GetStatusAsync"/>: one card per available provider for the settings shell.</item>
///   <item><see cref="LookupStateAsync"/>: learn the provider+redirectUri for a callback without consuming the row.</item>
///   <item><see cref="SetPullEnabledAsync"/>/<see cref="SetPushEnabledAsync"/>: per-direction toggles.</item>
/// </list>
///
/// <para>
/// Tokens are encrypted with <c>IDataProtector</c> (protector
/// <c>"RentalCommand.Accounting.v1"</c>) and persisted ONLY as cipher text (AC-4);
/// plaintext is never stored and tokens are never logged. The service uses the
/// scoped <see cref="RentalCommandDbContext"/> directly (RC's <c>BankingService</c>
/// pattern), not an <c>IDbContextFactory</c>.
/// </para>
/// </summary>
public class AccountingConnectionService
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    private readonly RentalCommandDbContext _db;
    private readonly IDataProtector _protector;
    private readonly AccountingProviderResolver _providerResolver;
    private readonly AccountingAppSettingsResolver _settingsResolver;
    private readonly AccountingImportService _importService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccountingConnectionService> _logger;

    public AccountingConnectionService(
        RentalCommandDbContext db,
        IDataProtectionProvider dataProtection,
        AccountingProviderResolver providerResolver,
        AccountingAppSettingsResolver settingsResolver,
        AccountingImportService importService,
        TimeProvider timeProvider,
        ILogger<AccountingConnectionService> logger)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("RentalCommand.Accounting.v1");
        _providerResolver = providerResolver;
        _settingsResolver = settingsResolver;
        _importService = importService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Begin a connect flow: persist a single-use <c>OAuthState</c> and return the
    /// provider's authorize URL. Fails closed with
    /// <see cref="AccountingNotConfiguredException"/> when the provider has no creds (AC-8).
    /// </summary>
    /// <param name="defaultRedirectUri">
    /// The request-derived callback URL. The provider's configured <c>RedirectUri</c>
    /// wins when present (it must byte-match the registered app); this is the fallback.
    /// The effective value is stored on the state row so the callback exchange reuses it.
    /// </param>
    public async Task<string> StartConnectAsync(
        int portfolioId, AccountingProvider provider, string defaultRedirectUri, CancellationToken ct)
    {
        var settings = _settingsResolver.Resolve(provider);
        if (!settings.Configured)
        {
            throw new AccountingNotConfiguredException(
                $"{provider} is not configured on this server. Set the provider's client id and secret.");
        }

        // Configured redirect URI wins (must byte-match the provider app); request URL is the fallback.
        var redirectUri = string.IsNullOrWhiteSpace(settings.RedirectUri)
            ? defaultRedirectUri
            : settings.RedirectUri!;

        // Clear any stale Pending rows for this provider — harmless, keeps state tidy.
        // Connected rows are never touched here.
        var stalePending = await _db.AccountingConnections
            .Where(c => c.PortfolioId == portfolioId
                && c.Provider == provider
                && c.Status == AccountingConnectionStatus.Pending)
            .ToListAsync(ct);
        if (stalePending.Count > 0)
        {
            _db.AccountingConnections.RemoveRange(stalePending);
        }

        var stateToken = GenerateBase64UrlToken(32);

        // PKCE: QuickBooks (provider #1) does not use it, so no code_verifier is staged here and
        // BuildAuthorizeUrl receives a null challenge. The CodeVerifier column + the codeChallenge
        // parameter exist so a PKCE provider (e.g. Xero) drops in later by staging a verifier — see
        // the Phase-2/5 note. We do not branch on the provider name to decide this.
        var stateRow = new OAuthState
        {
            PortfolioId = portfolioId,
            Provider = provider,
            StateToken = stateToken,
            RedirectUri = redirectUri,
            CodeVerifier = null,
            // Ephemeral OAuth state: the 10-min TTL and its expiry checks stay on the REAL clock (never
            // the simulation clock) so a time-travelling dev session can't wedge a live OAuth handshake.
            ExpiresAt = DateTime.UtcNow.Add(StateTtl),
            CreatedAt = DateTime.UtcNow,
        };
        _db.OAuthStates.Add(stateRow);
        await _db.SaveChangesAsync(ct);

        var prov = _providerResolver.Resolve(provider);
        return prov.BuildAuthorizeUrl(settings, redirectUri, stateToken, codeChallenge: null);
    }

    /// <summary>
    /// Complete the OAuth callback. The browser redirect back from the provider does
    /// NOT carry the caller's Bearer token, so the trusted scope comes from the
    /// single-use <c>OAuthState</c> row — the portfolio + provider are derived from
    /// it, never from a request parameter (D-10 / the Bearer-callback decision). The
    /// state token (256-bit, single-use, 10-min TTL) is the capability that binds the
    /// flow to the portfolio that started it.
    ///
    /// <para>Validates-and-consumes the state row, finds/creates the connection,
    /// exchanges the code via the resolved provider, encrypts+persists the tokens, and
    /// flips the connection to Connected. Returns the portfolio + provider so the
    /// controller can build its redirect.</para>
    /// </summary>
    public async Task<(int PortfolioId, AccountingProvider Provider)> CompleteCallbackFromStateAsync(
        AccountingCallback callback,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callback.State))
        {
            throw new InvalidOperationException(
                "Connection request expired or invalid, please try again");
        }

        // Look up by the single-use state token alone — the callback has no portfolio claim, so the
        // state row IS the trusted binding to (portfolio, provider, redirectUri). (Unauthenticated
        // requests run with the RLS admin bypass, so this read is not RLS-filtered; the random,
        // single-use, TTL'd token is what authorizes it.)
        var stateRow = await _db.OAuthStates
            .FirstOrDefaultAsync(s => s.StateToken == callback.State, ct);

        if (stateRow == null || stateRow.ExpiresAt < DateTime.UtcNow)
        {
            throw new InvalidOperationException(
                "Connection request expired or invalid, please try again");
        }

        var portfolioId = stateRow.PortfolioId;
        var provider = stateRow.Provider;
        var redirectUri = stateRow.RedirectUri;

        _db.OAuthStates.Remove(stateRow);
        await _db.SaveChangesAsync(ct);

        // Lazy-create the connection on callback (no setup modal for OAuth providers).
        var conn = await _db.AccountingConnections
            .FirstOrDefaultAsync(c => c.PortfolioId == portfolioId && c.Provider == provider, ct);
        if (conn == null)
        {
            conn = new AccountingConnection
            {
                PortfolioId = portfolioId,
                Provider = provider,
                Status = AccountingConnectionStatus.Pending,
                NextPullAtUtc = _timeProvider.UtcNow(),
                CreatedAt = _timeProvider.UtcNow(),
            };
            _db.AccountingConnections.Add(conn);
        }

        // The exchange must reuse the SAME redirect URI sent at authorize (stored on the state row).
        var settings = _settingsResolver.Resolve(provider, redirectUri);
        var prov = _providerResolver.Resolve(provider);
        var result = await prov.ExchangeCodeAsync(settings, callback, ct);

        // AC-4: encrypt at rest, plaintext never persisted.
        conn.AccessTokenCipherText = ProtectNullable(result.AccessToken);
        conn.RefreshTokenCipherText = ProtectNullable(result.RefreshToken);
        conn.TokenExpiresAt = result.ExpiresAtUtc;
        conn.ExternalAccountId = result.ExternalAccountId;
        conn.CompanyName = result.CompanyName;
        conn.Status = AccountingConnectionStatus.Connected;
        conn.LastError = null;
        conn.NextPullAtUtc = _timeProvider.UtcNow().AddMinutes(15);
        conn.ConnectedAt ??= _timeProvider.UtcNow();
        conn.DisconnectedAt = null;
        conn.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "AccountingConnection {ConnectionId} now Connected for portfolio {PortfolioId} ({Provider})",
            conn.Id, portfolioId, provider);

        // Import-on-connect: kick an initial pull so the landlord's money flows in immediately rather
        // than waiting for the first scheduled worker cycle. Best-effort — a failure here must never
        // fail the connect (the connection is already Connected; the worker will retry on its cadence).
        await RunInitialPullAsync(conn, ct);

        return (portfolioId, provider);
    }

    /// <summary>
    /// Best-effort initial import right after a successful connect (port of EdiPlatform's
    /// <c>RunInitialPullAsync</c>). Only runs when pull is enabled; swallows + logs failures so the
    /// connect flow is never broken by a transient provider error.
    /// </summary>
    private async Task RunInitialPullAsync(AccountingConnection conn, CancellationToken ct)
    {
        if (!conn.PullEnabled)
        {
            return;
        }

        try
        {
            var summary = await _importService.ImportAsync(conn, since: null, ct);
            _logger.LogInformation(
                "Initial accounting import for connection {ConnectionId}: {Payments} payments, {Expenses} expenses, {Review} for review",
                conn.Id, summary.PaymentsImported, summary.ExpensesImported, summary.NeedsReview);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Initial accounting import failed for connection {ConnectionId} — the scheduled pull worker will retry",
                conn.Id);
        }
    }

    /// <summary>Best-effort revoke at the provider, then flip Disconnected and blank the tokens.</summary>
    public async Task DisconnectAsync(int portfolioId, AccountingProvider provider, CancellationToken ct)
    {
        var conn = await _db.AccountingConnections
            .FirstOrDefaultAsync(c => c.PortfolioId == portfolioId && c.Provider == provider, ct);
        if (conn == null)
        {
            return;
        }

        // Revoke only when we can both resolve the provider and decrypt a refresh token.
        var refreshToken = UnprotectNullable(conn.RefreshTokenCipherText);
        if (!string.IsNullOrWhiteSpace(refreshToken) && _providerResolver.IsRegistered(provider))
        {
            try
            {
                var settings = _settingsResolver.Resolve(provider);
                var prov = _providerResolver.Resolve(provider);
                await prov.RevokeAsync(settings, refreshToken, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Provider revoke failed for AccountingConnection {ConnectionId} ({Provider}) — proceeding with local disconnect",
                    conn.Id, provider);
            }
        }

        conn.Status = AccountingConnectionStatus.Disconnected;
        conn.AccessTokenCipherText = null;
        conn.RefreshTokenCipherText = null;
        conn.TokenExpiresAt = null;
        conn.DisconnectedAt = _timeProvider.UtcNow();
        conn.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "AccountingConnection {ConnectionId} disconnected for portfolio {PortfolioId} ({Provider})",
            conn.Id, portfolioId, provider);
    }

    /// <summary>
    /// One status card per available provider (every <see cref="AccountingProvider"/>
    /// value), joined to the portfolio's connection row if one exists. Providers with
    /// no creds show <c>Configured=false</c> (AC-8); providers never connected show a
    /// null status. Review/imported counts are computed DB-side from the sync ledger.
    /// </summary>
    public async Task<List<AccountingConnectionStatusResponse>> GetStatusAsync(
        int portfolioId, CancellationToken ct)
    {
        var conns = await _db.AccountingConnections
            .Where(c => c.PortfolioId == portfolioId)
            .ToListAsync(ct);

        // DB-side counts over the ledger (no rows loaded to count): pending-review and imported,
        // grouped by connection. Empty in Phase 1 since nothing imports yet, but the query is correct.
        var reviewByConnection = await _db.AccountingSyncMaps
            .Where(m => m.PortfolioId == portfolioId
                && m.Direction == "Import"
                && (m.Status == "NeedsReview" || m.Status == "Unmatched"))
            .GroupBy(m => m.AccountingConnectionId)
            .Select(g => new { ConnectionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ConnectionId, x => x.Count, ct);

        var importedByConnection = await _db.AccountingSyncMaps
            .Where(m => m.PortfolioId == portfolioId
                && m.Direction == "Import"
                && m.Status == "Imported")
            .GroupBy(m => m.AccountingConnectionId)
            .Select(g => new { ConnectionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ConnectionId, x => x.Count, ct);

        var byProvider = conns.ToDictionary(c => c.Provider);

        var result = new List<AccountingConnectionStatusResponse>();
        foreach (var provider in Enum.GetValues<AccountingProvider>())
        {
            var settings = TryResolveSettings(provider);
            byProvider.TryGetValue(provider, out var conn);

            result.Add(new AccountingConnectionStatusResponse
            {
                Provider = provider,
                ProviderName = provider.ToString(),
                Configured = settings?.Configured ?? false,
                Status = conn?.Status,
                CompanyName = conn?.CompanyName,
                ConnectedAt = conn?.ConnectedAt,
                LastSyncedAt = conn?.LastSyncedAt,
                LastError = conn?.LastError,
                PullEnabled = conn?.PullEnabled ?? true,
                PushEnabled = conn?.PushEnabled ?? false,
                PendingReviewCount = conn != null && reviewByConnection.TryGetValue(conn.Id, out var r) ? r : 0,
                ImportedCount = conn != null && importedByConnection.TryGetValue(conn.Id, out var i) ? i : 0,
                Capabilities = TryGetCapabilities(provider),
            });
        }

        return result;
    }

    /// <summary>
    /// Resolve the provider + redirect URI a state token was issued for, without
    /// consuming the row. Throws the same generic error as the callback on a
    /// missing/expired row (no oracle for attackers).
    /// </summary>
    public async Task<(AccountingProvider Provider, string RedirectUri)> LookupStateAsync(
        int portfolioId, string stateToken, CancellationToken ct)
    {
        var row = await _db.OAuthStates
            .Where(s => s.StateToken == stateToken && s.PortfolioId == portfolioId)
            .Select(s => new { s.Provider, s.RedirectUri, s.ExpiresAt })
            .FirstOrDefaultAsync(ct);
        if (row == null || row.ExpiresAt < DateTime.UtcNow)
        {
            throw new InvalidOperationException(
                "Connection request expired or invalid, please try again");
        }

        return (row.Provider, row.RedirectUri);
    }

    /// <summary>Toggle the pull (accounting → Rental Command) direction for a connected provider.</summary>
    public Task SetPullEnabledAsync(int portfolioId, AccountingProvider provider, bool enabled, CancellationToken ct)
        => SetDirectionAsync(portfolioId, provider, pull: enabled, push: null, ct);

    /// <summary>Toggle the push (Rental Command → accounting) direction for a connected provider.</summary>
    public Task SetPushEnabledAsync(int portfolioId, AccountingProvider provider, bool enabled, CancellationToken ct)
        => SetDirectionAsync(portfolioId, provider, pull: null, push: enabled, ct);

    /// <summary>Set one or both direction toggles on the connection in a single update.</summary>
    public async Task SetDirectionAsync(
        int portfolioId, AccountingProvider provider, bool? pull, bool? push, CancellationToken ct)
    {
        var conn = await _db.AccountingConnections
            .FirstOrDefaultAsync(c => c.PortfolioId == portfolioId && c.Provider == provider, ct)
            ?? throw new InvalidOperationException(
                $"No {provider} connection to configure. Connect the provider first.");

        if (pull.HasValue)
        {
            conn.PullEnabled = pull.Value;
        }

        if (push.HasValue)
        {
            conn.PushEnabled = push.Value;
        }

        conn.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);
    }

    // --- Phase 2: import / mappings / confirm / review-queue ------------------------------

    /// <summary>
    /// Run a one-time / backfill import for a connected provider over an optional date range. Delegates
    /// to <see cref="AccountingImportService"/>; the date range is advisory (the provider pulls deltas,
    /// the import is idempotent). Returns the per-resource counts for the caller to surface.
    /// </summary>
    public async Task<AccountingImportService.ImportSummary> RunImportAsync(
        int portfolioId, AccountingProvider provider, DateTime? fromDate, DateTime? toDate, CancellationToken ct)
    {
        var conn = await _db.AccountingConnections
            .FirstOrDefaultAsync(c => c.PortfolioId == portfolioId && c.Provider == provider, ct)
            ?? throw new InvalidOperationException(
                $"No {provider} connection to import. Connect the provider first.");

        if (conn.Status != AccountingConnectionStatus.Connected)
        {
            throw new InvalidOperationException(
                $"{provider} is not connected (status: {conn.Status}). Reconnect before importing.");
        }

        // A backfill explicitly asks to re-scan from a start date, so honour fromDate as the delta floor
        // when supplied (null = use each resource's stored cursor).
        return await _importService.ImportAsync(conn, since: fromDate, ct);
    }

    /// <summary>
    /// The entity mappings (suggested + confirmed) for a connected provider, newest first. Projected
    /// DB-side. The UI shows these in the mapping-review panel so the landlord can confirm/adjust.
    /// </summary>
    public async Task<List<AccountingMappingResponse>> GetMappingsAsync(
        int portfolioId, AccountingProvider provider, bool? confirmed, int skip, int take, CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);

        var query = _db.AccountingEntityMappings
            .Where(m => m.PortfolioId == portfolioId
                && m.AccountingConnection!.Provider == provider);

        if (confirmed is not null)
        {
            query = query.Where(m => (m.ConfirmedAt != null) == confirmed.Value);
        }

        return await query
            .OrderByDescending(m => m.ConfirmedAt == null) // unconfirmed (needs attention) first
            .ThenByDescending(m => m.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .Select(m => new AccountingMappingResponse
            {
                Id = m.Id,
                ExternalType = m.ExternalType,
                ExternalId = m.ExternalId,
                ExternalDisplayName = m.ExternalDisplayName,
                LocalEntityType = m.LocalEntityType,
                LocalEntityId = m.LocalEntityId,
                LocalEnumValue = m.LocalEnumValue,
                Confidence = m.Confidence,
                Confirmed = m.ConfirmedAt != null,
                ConfirmedAt = m.ConfirmedAt,
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Confirm (or create) a landlord-chosen mapping between an external entity and a local one, then
    /// promote any transactions that were parked waiting on it (D-3). Stamps the confirming user for audit.
    /// </summary>
    public async Task<int> ConfirmMappingAsync(
        int portfolioId, AccountingProvider provider, int userId,
        ConfirmAccountingMappingRequest request, CancellationToken ct)
    {
        var conn = await _db.AccountingConnections
            .FirstOrDefaultAsync(c => c.PortfolioId == portfolioId && c.Provider == provider, ct)
            ?? throw new InvalidOperationException(
                $"No {provider} connection. Connect the provider first.");

        var mapping = await _db.AccountingEntityMappings
            .FirstOrDefaultAsync(m => m.PortfolioId == portfolioId
                && m.AccountingConnectionId == conn.Id
                && m.ExternalType == request.ExternalType
                && m.ExternalId == request.ExternalId, ct);

        if (mapping == null)
        {
            mapping = new AccountingEntityMapping
            {
                PortfolioId = portfolioId,
                AccountingConnectionId = conn.Id,
                ExternalType = request.ExternalType,
                ExternalId = request.ExternalId,
                ExternalDisplayName = request.ExternalDisplayName,
                CreatedAt = _timeProvider.UtcNow(),
            };
            _db.AccountingEntityMappings.Add(mapping);
        }

        mapping.LocalEntityType = request.LocalEntityType;
        mapping.LocalEntityId = request.LocalEntityId;
        mapping.LocalEnumValue = request.LocalEnumValue;
        mapping.ConfirmedAt = _timeProvider.UtcNow();
        mapping.ConfirmedByUserId = userId;
        mapping.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        // Promote any parked (NeedsReview/Unmatched) transactions now resolvable by this confirmation.
        return await _importService.RetryPendingForConnectionAsync(conn, ct);
    }

    /// <summary>
    /// The review queue: imported transactions that could not be auto-created (unmatched / needs-review)
    /// for a connected provider. The landlord confirms a mapping or creates the missing entity from here.
    /// </summary>
    public async Task<List<AccountingReviewItemResponse>> GetReviewQueueAsync(
        int portfolioId, AccountingProvider provider, int skip, int take, CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);

        return await _db.AccountingSyncMaps
            .Where(m => m.PortfolioId == portfolioId
                && m.AccountingConnection!.Provider == provider
                && m.Direction == LedgerDirection.Import
                && (m.Status == LedgerStatus.NeedsReview || m.Status == LedgerStatus.Unmatched))
            .OrderByDescending(m => m.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .Select(m => new AccountingReviewItemResponse
            {
                Id = m.Id,
                ExternalType = m.ExternalType,
                ExternalId = m.ExternalId,
                Status = m.Status,
                Reason = m.LastError,
            })
            .ToListAsync(ct);
    }

    private AccountingAppSettings? TryResolveSettings(AccountingProvider provider)
    {
        try
        {
            return _settingsResolver.Resolve(provider);
        }
        catch (InvalidOperationException)
        {
            // No options binding for this provider yet — treat as unconfigured for the status card.
            return null;
        }
    }

    private AccountingCapabilitiesDto? TryGetCapabilities(AccountingProvider provider)
    {
        if (!_providerResolver.IsRegistered(provider))
        {
            return null;
        }

        var c = _providerResolver.Resolve(provider).Capabilities;
        return new AccountingCapabilitiesDto
        {
            CanPullCustomers = c.CanPullCustomers,
            CanPullVendors = c.CanPullVendors,
            CanPullAccounts = c.CanPullAccounts,
            CanPullPayments = c.CanPullPayments,
            CanPullExpenses = c.CanPullExpenses,
            CanPushIncome = c.CanPushIncome,
            CanPushExpense = c.CanPushExpense,
        };
    }

    // --- Token-at-rest helpers (copied from BankingService:644-660) ------------------

    private string? ProtectNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : _protector.Protect(value);

    private string? UnprotectNullable(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Cryptographically-random base64url token for OAuth state (and PKCE verifiers later).</summary>
    private static string GenerateBase64UrlToken(int byteLength)
    {
        Span<byte> bytes = stackalloc byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
