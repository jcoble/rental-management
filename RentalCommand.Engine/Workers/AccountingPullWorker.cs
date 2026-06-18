using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Scheduled pull worker: every cycle, for each <see cref="AccountingConnectionStatus.Connected"/> +
/// <c>PullEnabled</c> accounting connection, pulls the latest deltas and imports them into the domain
/// via <see cref="AccountingImportService"/>. Ported from EdiPlatform's <c>ErpPullWorker</c> and
/// re-skinned to Rental Command's <see cref="EngineWorkerBase"/> (scoped <see cref="RentalCommandDbContext"/>
/// per cycle, not an <c>IDbContextFactory</c>).
///
/// <para>
/// A per-connection Postgres advisory lock keyed by <c>hashtext('acct_pull:' || id)::bigint</c> prevents
/// a connection being pulled twice at once (across Engine restarts or the import-on-connect path). The
/// per-resource isolation that EdiPlatform's worker did inline lives inside
/// <see cref="AccountingImportService.ImportAsync"/> here, so one resource failing does not abort the rest.
/// Provider-agnostic: the worker never names a provider — it only filters on status + capability.
/// </para>
/// </summary>
public sealed class AccountingPullWorker : EngineWorkerBase
{
    protected override string WorkerName => "AccountingPullWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromMinutes(15);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(10);

    public AccountingPullWorker(IServiceProvider serviceProvider, ILogger<AccountingPullWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var db = scoped.GetRequiredService<RentalCommandDbContext>();
        var import = scoped.GetRequiredService<AccountingImportService>();
        var logger = scoped.GetRequiredService<ILogger<AccountingPullWorker>>();

        // Only Connected + PullEnabled connections are eligible (off = manual/backfill-only). DB-side filter.
        var connections = await db.AccountingConnections
            .Where(c => c.Status == AccountingConnectionStatus.Connected && c.PullEnabled)
            .ToListAsync(ct);

        int processed = 0;
        foreach (var conn in connections)
        {
            if (!await TryAdvisoryLockAsync(db, conn.Id, ct))
            {
                logger.LogDebug("Accounting pull lock held; skipping connection {ConnectionId}", conn.Id);
                continue;
            }

            try
            {
                // since=null lets the import service read each resource's delta cursor from
                // LastPulledAtJson (a missing cursor = a first/full pull for that resource).
                var summary = await import.ImportAsync(conn, since: null, ct);
                processed += summary.PaymentsImported + summary.ExpensesImported;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Record the error on the connection but keep going with the next one.
                logger.LogError(ex,
                    "Accounting pull failed for connection {ConnectionId} — continuing with the next connection",
                    conn.Id);
                await RecordConnectionErrorAsync(scoped, conn.Id, ex, ct);
            }
            finally
            {
                await ReleaseAdvisoryLockAsync(db, conn.Id, logger, ct);
            }
        }

        return processed;
    }

    private static async Task RecordConnectionErrorAsync(
        IServiceProvider scoped, int connectionId, Exception ex, CancellationToken ct)
    {
        // Fresh scope so a failed import that left the cycle's context in a bad state can't block the write.
        using var errorScope = scoped.CreateScope();
        var db = errorScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var conn = await db.AccountingConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (conn == null)
        {
            return;
        }

        conn.Status = AccountingConnectionStatus.Error;
        conn.LastError = Truncate(ex.Message, 2000);
        conn.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    // --- Per-connection advisory lock (ported from ErpPullWorker) -----------------------------------

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
        p.Value = $"acct_pull:{connectionId}";
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
        p.Value = $"acct_pull:{connectionId}";
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
            logger.LogWarning(ex, "Failed to release accounting pull advisory lock for connection {ConnectionId}", connectionId);
        }
    }
}
