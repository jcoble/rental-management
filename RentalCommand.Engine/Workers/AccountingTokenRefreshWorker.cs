using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Proactively refreshes accounting OAuth access tokens before they expire so the continuous pull
/// (and a landlord's session) never hits a dead token. Polls every 5 minutes for every
/// <see cref="AccountingConnectionStatus.Connected"/> connection whose <c>TokenExpiresAt</c> is within
/// the next ~10 minutes and that still has a refresh token. Ported from EdiPlatform's
/// <c>ErpTokenRefreshWorker</c>, re-skinned to <see cref="EngineWorkerBase"/> (scoped
/// <see cref="RentalCommandDbContext"/>) + RC's encrypted-token model.
///
/// <para>
/// Per-connection Postgres advisory lock keyed by <c>hashtext('acct_refresh:'||id)::bigint</c> prevents
/// a race with the inline refresh-on-401 path. The actual decrypt → refresh → re-encrypt → persist (and
/// the dead-token → NeedsReconnect flip) lives in the shared <see cref="AccountingTokenService"/>, so the
/// worker only does eligibility + locking. Never throws out of a cycle; never logs tokens (AC-7).
/// </para>
/// </summary>
public sealed class AccountingTokenRefreshWorker : EngineWorkerBase
{
    private static readonly TimeSpan RefreshHorizon = TimeSpan.FromMinutes(10);

    protected override string WorkerName => "AccountingTokenRefreshWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromMinutes(5);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public AccountingTokenRefreshWorker(IServiceProvider serviceProvider, ILogger<AccountingTokenRefreshWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var db = scoped.GetRequiredService<RentalCommandDbContext>();
        var tokenService = scoped.GetRequiredService<AccountingTokenService>();
        var logger = scoped.GetRequiredService<ILogger<AccountingTokenRefreshWorker>>();

        var horizon = DateTime.UtcNow.Add(RefreshHorizon);

        // DB-side claim: only Connected connections with a refresh token whose access token is near expiry.
        // No load-then-loop — the filter is the whole eligibility test.
        var due = await db.AccountingConnections
            .Where(c => c.Status == AccountingConnectionStatus.Connected
                && c.RefreshTokenCipherText != null
                && c.TokenExpiresAt != null
                && c.TokenExpiresAt < horizon)
            .ToListAsync(ct);

        if (due.Count == 0)
        {
            return 0;
        }

        int refreshed = 0;
        foreach (var conn in due)
        {
            if (!await TryAdvisoryLockAsync(db, conn.Id, ct))
            {
                logger.LogDebug("Accounting refresh lock held; skipping connection {ConnectionId}", conn.Id);
                continue;
            }

            try
            {
                // Re-read under the lock: another worker / the 401-retry path may have just refreshed it.
                await db.Entry(conn).ReloadAsync(ct);
                if (conn.Status != AccountingConnectionStatus.Connected
                    || conn.TokenExpiresAt == null
                    || conn.TokenExpiresAt >= horizon)
                {
                    continue;
                }

                var result = await tokenService.RefreshAsync(db, conn, ct);
                if (result.Outcome == AccountingTokenService.RefreshOutcome.Refreshed)
                {
                    refreshed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transient failure — record on the connection and keep going with the next one.
                logger.LogError(ex,
                    "Accounting token refresh failed for connection {ConnectionId} — continuing with the next connection",
                    conn.Id);
                await RecordConnectionErrorAsync(scoped, conn.Id, ex, ct);
            }
            finally
            {
                await ReleaseAdvisoryLockAsync(db, conn.Id, logger, ct);
            }
        }

        return refreshed;
    }

    private static async Task RecordConnectionErrorAsync(
        IServiceProvider scoped, int connectionId, Exception ex, CancellationToken ct)
    {
        using var errorScope = scoped.CreateScope();
        var db = errorScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var conn = await db.AccountingConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (conn == null)
        {
            return;
        }

        conn.Status = AccountingConnectionStatus.Error;
        conn.LastError = Truncate(ex.Message, 2000);
        conn.UpdatedAt = scoped.GetRequiredService<TimeProvider>().UtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    // --- Per-connection advisory lock (same shape as AccountingPullWorker) --------------------------

    private static async Task<bool> TryAdvisoryLockAsync(RentalCommandDbContext db, int connectionId, CancellationToken ct)
    {
        var dbConn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (dbConn.State != System.Data.ConnectionState.Open)
        {
            await dbConn.OpenAsync(ct);
        }

        await using var cmd = dbConn.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(hashtext(@key)::bigint)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@key";
        p.Value = $"acct_refresh:{connectionId}";
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool b && b;
    }

    private static async Task ReleaseAdvisoryLockAsync(
        RentalCommandDbContext db, int connectionId, ILogger logger, CancellationToken ct)
    {
        var dbConn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (dbConn.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        await using var cmd = dbConn.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(hashtext(@key)::bigint)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@key";
        p.Value = $"acct_refresh:{connectionId}";
        cmd.Parameters.Add(p);
        try
        {
            await cmd.ExecuteScalarAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release accounting refresh advisory lock for connection {ConnectionId}", connectionId);
        }
    }
}
