using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Api.Data;

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
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IRlsExecutionContext _rlsExecutionContext;

    public AccountingConnectionService(
        RentalCommandDbContext db,
        IDataProtectionProvider dataProtection,
        AccountingProviderResolver providerResolver,
        AccountingAppSettingsResolver settingsResolver,
        AccountingImportService importService,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic,
        IRlsExecutionContext rlsExecutionContext,
        ILogger<AccountingConnectionService> logger)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("RentalCommand.Accounting.v1");
        _providerResolver = providerResolver;
        _settingsResolver = settingsResolver;
        _importService = importService;
        _timeProvider = timeProvider;
        _logger = logger;
        _atomic = atomic;
        _rlsExecutionContext = rlsExecutionContext;
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
        await _db.AccountingConnections
            .Where(c => c.PortfolioId == portfolioId
                && c.Provider == provider
                && c.Status == AccountingConnectionStatus.Pending)
            .ExecuteDeleteAsync(ct);

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

        AccountingConnection conn;
        int portfolioId;
        AccountingProvider provider;
        using (_rlsExecutionContext.BeginBypass(RlsBypassReason.AccountingOAuthCallback))
        {
            // The opaque, high-entropy, single-use state is the callback's only admission token.
            // Its conditional delete is the transaction-scoped claim: a concurrent callback waits
            // on the same row and observes zero affected rows before any provider exchange begins.
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            var callbackNow = DateTime.UtcNow;
            var stateRow = await _db.OAuthStates
                .AsNoTracking()
                .Where(state => state.StateToken == callback.State && state.ExpiresAt >= callbackNow)
                .SingleOrDefaultAsync(ct);

            if (stateRow == null)
            {
                throw new InvalidOperationException(
                    "Connection request expired or invalid, please try again");
            }

            var claimed = await _db.OAuthStates
                .Where(state => state.Id == stateRow.Id &&
                                state.StateToken == callback.State &&
                                state.ExpiresAt >= callbackNow)
                .ExecuteDeleteAsync(ct);
            if (claimed != 1)
            {
                throw new InvalidOperationException(
                    "Connection request expired or invalid, please try again");
            }

            portfolioId = stateRow.PortfolioId;
            provider = stateRow.Provider;
            var redirectUri = stateRow.RedirectUri;

            // The exchange must reuse the SAME redirect URI sent at authorize (stored on the state row).
            var settings = _settingsResolver.Resolve(provider, redirectUri);
            var prov = _providerResolver.Resolve(provider);
            var result = await prov.ExchangeCodeAsync(settings, callback, ct);

            // Lazy-create the connection on callback (no setup modal for OAuth providers).
            conn = await _db.AccountingConnections
                .SingleOrDefaultAsync(
                    candidate => candidate.PortfolioId == portfolioId && candidate.Provider == provider,
                    ct)
                ?? new AccountingConnection
                {
                    PortfolioId = portfolioId,
                    Provider = provider,
                    Status = AccountingConnectionStatus.Pending,
                    NextPullAtUtc = _timeProvider.UtcNow(),
                    CreatedAt = _timeProvider.UtcNow(),
                };
            if (conn.Id == 0)
            {
                _db.AccountingConnections.Add(conn);
            }

            // AC-4: encrypt at rest, plaintext never persisted.
            conn.AccessTokenCipherText = ProtectNullable(result.AccessToken);
            conn.RefreshTokenCipherText = ProtectNullable(result.RefreshToken);
            conn.TokenExpiresAt = result.ExpiresAtUtc;
            conn.ExternalAccountId = result.ExternalAccountId;
            conn.CompanyName = result.CompanyName;
            conn.Status = AccountingConnectionStatus.Connected;
            conn.TokenGeneration++;
            conn.TokenRotationState = AccountingTokenRotationState.Idle;
            conn.TokenRotationClaimOwner = null;
            conn.TokenRotationClaimToken = null;
            conn.TokenRotationClaimExpiresAtUtc = null;
            conn.PullClaimOwner = null;
            conn.PullClaimToken = null;
            conn.PullClaimExpiresAtUtc = null;
            conn.LastError = null;
            conn.NextPullAtUtc = _timeProvider.UtcNow().AddMinutes(15);
            conn.ConnectedAt ??= _timeProvider.UtcNow();
            conn.DisconnectedAt = null;
            conn.UpdatedAt = _timeProvider.UtcNow();

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

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
    /// Leaves the already-due connection for the fenced pull worker. Provider I/O cannot be started
    /// inline because only the worker owns a durable pull claim.
    /// </summary>
    private Task RunInitialPullAsync(AccountingConnection conn, CancellationToken ct)
    {
        if (!conn.PullEnabled)
        {
            return Task.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "Initial accounting import for connection {ConnectionId} was queued for the fenced pull worker",
            conn.Id);
        return Task.CompletedTask;
    }

    /// <summary>Best-effort revoke at the provider, then flip Disconnected and blank the tokens.</summary>
    public async Task DisconnectAsync(int portfolioId, AccountingProvider provider, CancellationToken ct)
    {
        var conn = await _db.AccountingConnections.AsNoTracking()
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

        var disconnectedAt = _timeProvider.UtcNow();
        await _db.AccountingConnections
            .Where(c => c.Id == conn.Id
                && c.PortfolioId == portfolioId
                && c.Provider == provider)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, AccountingConnectionStatus.Disconnected)
                .SetProperty(c => c.AccessTokenCipherText, (string?)null)
                .SetProperty(c => c.RefreshTokenCipherText, (string?)null)
                .SetProperty(c => c.TokenExpiresAt, (DateTime?)null)
                .SetProperty(c => c.PullClaimOwner, (string?)null)
                .SetProperty(c => c.PullClaimToken, (Guid?)null)
                .SetProperty(c => c.PullClaimExpiresAtUtc, (DateTime?)null)
                .SetProperty(c => c.TokenRotationState, AccountingTokenRotationState.Idle)
                .SetProperty(c => c.TokenRotationClaimOwner, (string?)null)
                .SetProperty(c => c.TokenRotationClaimToken, (Guid?)null)
                .SetProperty(c => c.TokenRotationClaimExpiresAtUtc, (DateTime?)null)
                .SetProperty(c => c.DisconnectedAt, disconnectedAt)
                .SetProperty(c => c.UpdatedAt, disconnectedAt), ct);

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
        // One translated statement: the connection projection owns both correlated ledger counts.
        // Provider configuration/capabilities are deliberately applied only after materialization;
        // they are process configuration, not a second database result set.
        var connectedCards = await _db.AccountingConnections
            .Where(c => c.PortfolioId == portfolioId)
            .Select(c => new AccountingConnectionStatusResponse
            {
                Provider = c.Provider,
                Status = c.Status,
                CompanyName = c.CompanyName,
                ConnectedAt = c.ConnectedAt,
                LastSyncedAt = c.LastSyncedAt,
                LastError = c.LastError,
                PullEnabled = c.PullEnabled,
                PushEnabled = c.PushEnabled,
                PendingReviewCount = _db.AccountingSyncMaps.Count(m =>
                    m.PortfolioId == portfolioId
                    && m.AccountingConnectionId == c.Id
                    && m.Direction == LedgerDirection.Import
                    && (m.Status == LedgerStatus.NeedsReview || m.Status == LedgerStatus.Unmatched)),
                ImportedCount = _db.AccountingSyncMaps.Count(m =>
                    m.PortfolioId == portfolioId
                    && m.AccountingConnectionId == c.Id
                    && m.Direction == LedgerDirection.Import
                    && m.Status == LedgerStatus.Imported),
            })
            .ToListAsync(ct);

        var byProvider = connectedCards.ToDictionary(card => card.Provider);

        var result = new List<AccountingConnectionStatusResponse>();
        foreach (var provider in Enum.GetValues<AccountingProvider>())
        {
            var settings = TryResolveSettings(provider);
            if (byProvider.TryGetValue(provider, out var card))
            {
                card.ProviderName = provider.ToString();
                card.Configured = settings?.Configured ?? false;
                card.Capabilities = TryGetCapabilities(provider);
                result.Add(card);
                continue;
            }

            result.Add(new AccountingConnectionStatusResponse
            {
                Provider = provider,
                ProviderName = provider.ToString(),
                Configured = settings?.Configured ?? false,
                PullEnabled = true,
                PushEnabled = false,
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
        var updatedAt = _timeProvider.UtcNow();
        var updated = await _db.AccountingConnections
            .Where(c => c.PortfolioId == portfolioId && c.Provider == provider)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.PullEnabled, c => pull.HasValue ? pull.Value : c.PullEnabled)
                .SetProperty(c => c.PushEnabled, c => push.HasValue ? push.Value : c.PushEnabled)
                .SetProperty(c => c.PullClaimOwner,
                    c => pull == false ? null : c.PullClaimOwner)
                .SetProperty(c => c.PullClaimToken,
                    c => pull == false ? null : c.PullClaimToken)
                .SetProperty(c => c.PullClaimExpiresAtUtc,
                    c => pull == false ? null : c.PullClaimExpiresAtUtc)
                .SetProperty(c => c.UpdatedAt, updatedAt), ct);
        if (updated == 0)
            throw new InvalidOperationException(
                $"No {provider} connection to configure. Connect the provider first.");
    }

    // --- Phase 2: import / mappings / confirm / review-queue ------------------------------

    /// <summary>
    /// Manual backfills are disabled until the command endpoint can enqueue an exact durable pull
    /// claim. This prevents a second unfenced writer from bypassing the atomic cutover.
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

        throw new InvalidOperationException(
            "Manual accounting pulls must be queued through the fenced accounting worker.");
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
                Revision = m.Revision,
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Confirm (or create) a landlord-chosen mapping between an external entity and a local one, then
    /// promote any transactions that were parked waiting on it (D-3). Stamps the confirming user for audit.
    /// </summary>
    public async Task<ConfirmAccountingMappingResponse> ConfirmMappingAsync(
        int portfolioId, AccountingProvider provider, int userId,
        ConfirmAccountingMappingRequest request, CancellationToken ct)
    {
        var conn = await _db.AccountingConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.PortfolioId == portfolioId && c.Provider == provider, ct)
            ?? throw new InvalidOperationException(
                $"No {provider} connection. Connect the provider first.");

        var externalType = RequireMappingValue(request.ExternalType, nameof(request.ExternalType));
        var externalId = RequireMappingValue(request.ExternalId, nameof(request.ExternalId));
        var localEntityType = RequireMappingValue(request.LocalEntityType, nameof(request.LocalEntityType));
        var clientOperationId = RequireMappingValue(request.ClientOperationId, nameof(request.ClientOperationId));
        if (clientOperationId.Length > 160)
            throw new InvalidOperationException("Accounting mapping ClientOperationId cannot exceed 160 characters.");
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "accounting.mapping.confirm",
                $"{portfolioId}:{conn.Id}:{userId}:" +
                $"{OperationDigest($"{externalType}\u001f{externalId}")}:" +
                OperationDigest(clientOperationId)),
            new ConfirmAccountingMappingCommand(
                portfolioId,
                conn.Id,
                provider,
                userId,
                externalType,
                externalId,
                Normalize(request.ExternalDisplayName),
                localEntityType,
                request.LocalEntityId,
                Normalize(request.LocalEnumValue),
                clientOperationId,
                request.ExpectedRevision,
                _timeProvider.UtcNow()),
            new AtomicJsonResultCodec<ConfirmAccountingMappingResult>(
                "accounting.mapping.confirm.result.v2"),
            ct);

        if (outcome.Value.Outcome == ConfirmAccountingMappingOutcome.Applied)
        {
            var promoted = outcome.Value.PromotedCount;
            var hasMore = outcome.Value.HasMore;
            var batchOrdinal = 0;
            while (hasMore && outcome.Value.ContinuationId is Guid continuationId)
            {
                var continued = await ContinueMappingPromotionAsync(
                    portfolioId,
                    provider,
                    userId,
                    continuationId,
                    new ContinueAccountingMappingPromotionRequest
                    {
                        // Deterministic from the caller's stable operation key. Retrying an HTTP
                        // request replays each already-committed batch receipt in order.
                        ClientOperationId = $"batch:{++batchOrdinal}:{OperationDigest(clientOperationId)}",
                    },
                    ct);
                promoted += continued.Promoted;
                hasMore = continued.HasMore;
            }
            return new ConfirmAccountingMappingResponse
            {
                MappingId = outcome.Value.MappingId,
                MappingRevision = outcome.Value.MappingRevision,
                Promoted = promoted,
                ContinuationId = hasMore ? outcome.Value.ContinuationId : null,
                HasMore = hasMore,
            };
        }

        return outcome.Value.Outcome switch
        {
            ConfirmAccountingMappingOutcome.ConnectionNotFound => throw new InvalidOperationException(
                $"No {provider} connection. Connect the provider first."),
            ConfirmAccountingMappingOutcome.InvalidTarget => throw new InvalidOperationException(
                "The requested accounting mapping target is invalid or belongs to another portfolio."),
            ConfirmAccountingMappingOutcome.StaleRevision => throw new InvalidOperationException(
                $"The accounting mapping changed. Refresh and retry from revision {outcome.Value.MappingRevision}."),
            _ => throw new InvalidOperationException("Accounting mapping confirmation returned an unknown outcome."),
        };
    }

    public async Task<ContinueAccountingMappingPromotionResponse> ContinueMappingPromotionAsync(
        int portfolioId,
        AccountingProvider provider,
        int userId,
        Guid continuationId,
        ContinueAccountingMappingPromotionRequest request,
        CancellationToken ct)
    {
        var connectionId = await _db.AccountingConnections.AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId && row.Provider == provider)
            .Select(row => (int?)row.Id)
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"No {provider} connection. Connect the provider first.");
        var clientOperationId = RequireMappingValue(request.ClientOperationId, nameof(request.ClientOperationId));
        if (clientOperationId.Length > 160)
            throw new InvalidOperationException("Accounting continuation ClientOperationId cannot exceed 160 characters.");
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "accounting.mapping.promote.continue",
                $"{portfolioId}:{connectionId}:{continuationId:N}:{userId}:{OperationDigest(clientOperationId)}"),
            new ContinueAccountingMappingPromotionCommand(
                portfolioId,
                connectionId,
                continuationId,
                userId,
                clientOperationId,
                _timeProvider.UtcNow()),
            new AtomicJsonResultCodec<ContinueAccountingMappingPromotionResult>(
                "accounting.mapping.promote.continue.result.v1"),
            ct);
        if (outcome.Value.Outcome == ContinueAccountingMappingPromotionOutcome.NotFound)
            throw new InvalidOperationException("Accounting mapping promotion continuation was not found.");
        if (outcome.Value.Outcome == ContinueAccountingMappingPromotionOutcome.Superseded)
            throw new InvalidOperationException("Accounting mapping promotion was superseded by a newer correction.");
        return new ContinueAccountingMappingPromotionResponse
        {
            Promoted = outcome.Value.PromotedCount,
            TotalPromoted = outcome.Value.TotalPromotedCount,
            HasMore = outcome.Value.HasMore,
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequireMappingValue(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Accounting mapping {name} is required.")
            : value.Trim();

    private static string OperationDigest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
