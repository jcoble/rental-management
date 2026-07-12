using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Time;
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
/// a reconnect instead of failing silently. Every caller must first acquire the same durable
/// token-rotation claim; the provider call then runs outside a database transaction and completion
/// is a single fenced SQL update.
/// </para>
/// </summary>
public sealed class AccountingTokenService
{
    private readonly IDataProtector _protector;
    private readonly AccountingProviderResolver _providerResolver;
    private readonly AccountingAppSettingsResolver _settingsResolver;
    private readonly TimeProvider _timeProvider;
    private readonly IAccountingConnectionClaimStore _claims;
    private readonly ILogger<AccountingTokenService> _logger;

    public AccountingTokenService(
        IDataProtectionProvider dataProtection,
        AccountingProviderResolver providerResolver,
        AccountingAppSettingsResolver settingsResolver,
        IAccountingConnectionClaimStore claims,
        TimeProvider timeProvider,
        ILogger<AccountingTokenService> logger)
    {
        _protector = dataProtection.CreateProtector("RentalCommand.Accounting.v1");
        _providerResolver = providerResolver;
        _settingsResolver = settingsResolver;
        _claims = claims;
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
    /// (both tokens rotate on QuickBooks). Returns the new decrypted access token so a 401-retry
    /// caller can reuse it. If provider success cannot be durably persisted, the connection is put
    /// in an explicit reconnect-required recovery state instead of retrying the consumed token.
    ///
    /// <para>On <see cref="AccountingReconnectRequiredException"/> the connection is flipped to
    /// NeedsReconnect (tokens blanked) and the method returns <see cref="RefreshOutcome.NeedsReconnect"/>
    /// rather than throwing — a dead refresh token is an expected, recoverable state, not a cycle error.
    /// Other provider failures are conservatively treated as ambiguous because the remote side may
    /// have consumed the rotating token before the response was lost.</para>
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(
        AccountingConnection connection,
        AccountingWorkerFence rotationFence,
        CancellationToken ct)
    {
        if (rotationFence.Operation != AccountingWorkerOperation.TokenRotation)
            throw new InvalidOperationException("Token refresh requires the shared token-rotation fence.");

        var refreshToken = UnprotectNullable(connection.RefreshTokenCipherText);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            await MarkNeedsReconnectAsync(
                connection, rotationFence, "No refresh token on the connection.", ct);
            return new RefreshResult(RefreshOutcome.NeedsReconnect, null, null);
        }

        var settings = _settingsResolver.Resolve(connection.Provider);
        var provider = _providerResolver.Resolve(connection.Provider);

        try
        {
            var result = await provider.RefreshTokenAsync(settings, refreshToken, ct);
            var accessCipherText = ProtectNullable(result.AccessToken)!;
            var refreshCipherText = ProtectNullable(result.RefreshToken)!;
            int completed;
            try
            {
                completed = await _claims.CompleteTokenRotationAsync(
                    connection.Id, rotationFence.ClaimToken, rotationFence.ParentPullClaimToken,
                    accessCipherText, refreshCipherText,
                    result.ExpiresAtUtc, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Provider rotated tokens for AccountingConnection {ConnectionId}, but local persistence failed; reconnect is required",
                    connection.Id);
                await MarkNeedsReconnectAsync(connection, rotationFence,
                    "Provider rotated the token, but Rental Command could not confirm the new token. Reconnect the accounting provider.", ct);
                return new RefreshResult(RefreshOutcome.NeedsReconnect, null, null);
            }

            if (completed == 0)
            {
                await MarkNeedsReconnectAsync(connection, rotationFence,
                    "Provider rotated the token, but the rotation claim was no longer current. Reconnect the accounting provider.", ct);
                throw new DbUpdateConcurrencyException("The accounting token-rotation claim is no longer owned.");
            }

            connection.AccessTokenCipherText = accessCipherText;
            connection.RefreshTokenCipherText = refreshCipherText;
            connection.TokenExpiresAt = result.ExpiresAtUtc;
            connection.TokenGeneration++;
            connection.TokenRotationState = AccountingTokenRotationState.Idle;
            connection.TokenRotationClaimOwner = null;
            connection.TokenRotationClaimToken = null;
            connection.TokenRotationClaimExpiresAtUtc = null;
            connection.LastError = null;
            connection.UpdatedAt = _timeProvider.UtcNow();

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
                connection, rotationFence, "Refresh token expired — please reconnect.", ct);
            return new RefreshResult(RefreshOutcome.NeedsReconnect, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not DbUpdateConcurrencyException)
        {
            await MarkNeedsReconnectAsync(connection, rotationFence,
                "Token rotation may have reached the provider but could not be confirmed. Reconnect the accounting provider.", ct);
            throw;
        }
    }

    private async Task MarkNeedsReconnectAsync(
        AccountingConnection connection,
        AccountingWorkerFence rotationFence,
        string reason,
        CancellationToken ct)
    {
        var updated = await _claims.MarkTokenRotationRecoveryRequiredAsync(
            connection.Id, rotationFence.ClaimToken, reason, ct);
        if (updated == 0) return; // Disconnect/revocation already established a safer terminal state.
        connection.Status = AccountingConnectionStatus.NeedsReconnect;
        connection.AccessTokenCipherText = null;
        connection.RefreshTokenCipherText = null;
        connection.TokenExpiresAt = null;
        connection.TokenRotationState = AccountingTokenRotationState.RecoveryRequired;
        connection.LastError = reason;
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
