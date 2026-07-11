using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// The ONE provider-agnostic place that refreshes an accounting connection's OAuth tokens and
/// persists them. Used by BOTH the scheduled <c>AccountingTokenRefreshWorker</c> (proactive, before
/// expiry) and the inline refresh-on-401 in the pull/import path (reactive, when a token died
/// mid-run). Keeping the decrypt → refresh → re-encrypt → persist logic here means the providers stay
/// stateless about persistence and nothing duplicates the token-rotation handling (AC-7).
///
/// <para>
/// Tokens are read/written ONLY as cipher text via the same <c>IDataProtector</c> the connection
/// service used (<c>"RentalCommand.Accounting.v1"</c>, AC-4); they are never logged. On a dead refresh
/// token (<see cref="AccountingReconnectRequiredException"/> — provider-neutral) the connection flips to
/// <see cref="AccountingConnectionStatus.NeedsReconnect"/> and the tokens are blanked, so the UI prompts
/// a reconnect instead of failing silently. The caller persists via the SAME <see cref="RentalCommandDbContext"/>
/// it passed in (the connection is already tracked there).
/// </para>
/// </summary>
public sealed class AccountingTokenService
{
    private readonly IDataProtector _protector;
    private readonly AccountingProviderResolver _providerResolver;
    private readonly AccountingAppSettingsResolver _settingsResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccountingTokenService> _logger;

    public AccountingTokenService(
        IDataProtectionProvider dataProtection,
        AccountingProviderResolver providerResolver,
        AccountingAppSettingsResolver settingsResolver,
        TimeProvider timeProvider,
        ILogger<AccountingTokenService> logger)
    {
        _protector = dataProtection.CreateProtector("RentalCommand.Accounting.v1");
        _providerResolver = providerResolver;
        _settingsResolver = settingsResolver;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The outcome of a refresh attempt.</summary>
    public enum RefreshOutcome
    {
        /// <summary>Tokens rotated and persisted; <see cref="RefreshResult.AccessToken"/> is the new decrypted access token.</summary>
        Refreshed,

        /// <summary>The refresh token was dead — the connection was flipped to NeedsReconnect and tokens blanked.</summary>
        NeedsReconnect,
    }

    /// <summary>Result of <see cref="RefreshAsync"/>; the decrypted access token is present only on <see cref="RefreshOutcome.Refreshed"/>.</summary>
    public sealed record RefreshResult(RefreshOutcome Outcome, string? AccessToken, DateTime? ExpiresAtUtc);

    /// <summary>
    /// Refresh <paramref name="connection"/>'s tokens through its provider and persist the rotated pair
    /// (both tokens rotate on QuickBooks). Mutates the tracked entity and calls SaveChanges on
    /// <paramref name="db"/>. Returns the new decrypted access token so a 401-retry caller can reuse it.
    ///
    /// <para>On <see cref="AccountingReconnectRequiredException"/> the connection is flipped to
    /// NeedsReconnect (tokens blanked) and the method returns <see cref="RefreshOutcome.NeedsReconnect"/>
    /// rather than throwing — a dead refresh token is an expected, recoverable state, not a cycle error.
    /// Transient failures still throw so the worker records an Error and retries next cycle.</para>
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(
        RentalCommandDbContext db,
        AccountingConnection connection,
        CancellationToken ct,
        AccountingWorkerFence? workerFence = null)
    {
        var refreshToken = UnprotectNullable(connection.RefreshTokenCipherText);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            // No refresh token to use — treat as needing a reconnect (can't recover without one).
            await MarkNeedsReconnectAsync(db, connection, "No refresh token on the connection.", workerFence, ct);
            return new RefreshResult(RefreshOutcome.NeedsReconnect, null, null);
        }

        var settings = _settingsResolver.Resolve(connection.Provider);
        var provider = _providerResolver.Resolve(connection.Provider);

        try
        {
            var result = await provider.RefreshTokenAsync(settings, refreshToken, ct);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockOwnedConnectionAsync(db, connection.Id, workerFence, ct);
            connection.AccessTokenCipherText = ProtectNullable(result.AccessToken);
            connection.RefreshTokenCipherText = ProtectNullable(result.RefreshToken);
            connection.TokenExpiresAt = result.ExpiresAtUtc;
            connection.LastError = null;
            // A successful refresh recovers a connection that had errored.
            if (connection.Status == AccountingConnectionStatus.Error)
            {
                connection.Status = AccountingConnectionStatus.Connected;
            }

            connection.UpdatedAt = _timeProvider.UtcNow();
            ClearCompletedRefreshClaim(connection, workerFence);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "Refreshed tokens for AccountingConnection {ConnectionId} ({Provider})",
                connection.Id, connection.Provider);

            return new RefreshResult(RefreshOutcome.Refreshed, result.AccessToken, result.ExpiresAtUtc);
        }
        catch (AccountingReconnectRequiredException ex)
        {
            _logger.LogWarning(
                "Refresh token dead for AccountingConnection {ConnectionId} ({Provider}); flipping to NeedsReconnect: {Reason}",
                connection.Id, connection.Provider, ex.Message);
            await MarkNeedsReconnectAsync(
                db, connection, "Refresh token expired — please reconnect.", workerFence, ct);
            return new RefreshResult(RefreshOutcome.NeedsReconnect, null, null);
        }
    }

    private async Task MarkNeedsReconnectAsync(
        RentalCommandDbContext db,
        AccountingConnection connection,
        string reason,
        AccountingWorkerFence? workerFence,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockOwnedConnectionAsync(db, connection.Id, workerFence, ct);
        connection.Status = AccountingConnectionStatus.NeedsReconnect;
        connection.AccessTokenCipherText = null;
        connection.RefreshTokenCipherText = null;
        connection.TokenExpiresAt = null;
        connection.LastError = reason;
        connection.UpdatedAt = _timeProvider.UtcNow();
        ClearCompletedRefreshClaim(connection, workerFence);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task LockOwnedConnectionAsync(
        RentalCommandDbContext db,
        int connectionId,
        AccountingWorkerFence? workerFence,
        CancellationToken ct)
    {
        if (workerFence is null) return;

        var query = db.AccountingConnections.IgnoreQueryFilters().Where(row => row.Id == connectionId);
        var owned = workerFence.Operation switch
        {
            AccountingWorkerOperation.Pull => await query
                .Where(row => row.PullClaimToken == workerFence.ClaimToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.PullClaimExpiresAtUtc, row => row.PullClaimExpiresAtUtc), ct),
            AccountingWorkerOperation.Refresh => await query
                .Where(row => row.RefreshClaimToken == workerFence.ClaimToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.RefreshClaimExpiresAtUtc, row => row.RefreshClaimExpiresAtUtc), ct),
            _ => 0,
        };
        if (owned == 0)
            throw new DbUpdateConcurrencyException("The accounting token-refresh claim is no longer owned.");
    }

    private static void ClearCompletedRefreshClaim(
        AccountingConnection connection,
        AccountingWorkerFence? workerFence)
    {
        if (workerFence?.Operation != AccountingWorkerOperation.Refresh) return;
        connection.RefreshClaimOwner = null;
        connection.RefreshClaimToken = null;
        connection.RefreshClaimExpiresAtUtc = null;
    }

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
}
