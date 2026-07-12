using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Simulation;

public sealed record SimWorkerCommandClaim(Guid Id, string WorkerKey, string ClaimOwner, Guid ClaimToken);

public interface ISimWorkerCommandClaimStore
{
    Task<SimWorkerCommandClaim?> ClaimOldestAsync(
        string claimOwner, TimeSpan leaseDuration, CancellationToken ct = default);
    Task<int> MarkDoneAsync(
        Guid id, string claimOwner, Guid claimToken, string resultJson, DateTime completedAtUtc,
        CancellationToken ct = default);
    Task<int> MarkErrorAsync(
        Guid id, string claimOwner, Guid claimToken, string error, DateTime completedAtUtc,
        CancellationToken ct = default);
}

/// <summary>
/// One-statement, DB-clock leased simulation queue with owner/token/live-lease terminal fencing.
/// </summary>
public sealed class SimWorkerCommandClaimStore : ISimWorkerCommandClaimStore
{
    private const string ClaimSql = """
        WITH clock AS MATERIALIZED (
            SELECT clock_timestamp() AS now_utc
        ), candidate AS (
            SELECT command."Id"
            FROM "SimWorkerCommands" AS command
            CROSS JOIN clock
            WHERE (command."Status" = 'Pending'
                   OR (command."Status" = 'Running' AND command."ClaimExpiresAtUtc" <= clock.now_utc))
              AND (command."ClaimToken" IS NULL OR command."ClaimExpiresAtUtc" <= clock.now_utc)
            ORDER BY command."CreatedRealUtc", command."Id"
            FOR UPDATE OF command SKIP LOCKED
            LIMIT 1
        )
        UPDATE "SimWorkerCommands" AS command
        SET "Status" = 'Running',
            "ClaimOwner" = @claimOwner,
            "ClaimToken" = gen_random_uuid(),
            "ClaimExpiresAtUtc" = clock.now_utc + @leaseDuration,
            "AttemptCount" = command."AttemptCount" + 1,
            "LastAttemptAtUtc" = clock.now_utc,
            "Error" = NULL
        FROM candidate, clock
        WHERE command."Id" = candidate."Id"
        RETURNING command."Id", command."WorkerKey", command."ClaimOwner", command."ClaimToken";
        """;

    private const string MarkDoneSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "SimWorkerCommands" AS command
        SET "Status" = 'Done',
            "ResultJson" = CAST(@resultJson AS jsonb),
            "Error" = NULL,
            "CompletedRealUtc" = @completedRealUtc,
            "ClaimOwner" = NULL,
            "ClaimToken" = NULL,
            "ClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE command."Id" = @id
          AND command."Status" = 'Running'
          AND command."ClaimOwner" = @claimOwner
          AND command."ClaimToken" = @claimToken
          AND command."ClaimExpiresAtUtc" > clock.now_utc;
        """;

    private const string MarkErrorSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "SimWorkerCommands" AS command
        SET "Status" = 'Error',
            "Error" = @error,
            "CompletedRealUtc" = @completedRealUtc,
            "ClaimOwner" = NULL,
            "ClaimToken" = NULL,
            "ClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE command."Id" = @id
          AND command."Status" = 'Running'
          AND command."ClaimOwner" = @claimOwner
          AND command."ClaimToken" = @claimToken
          AND command."ClaimExpiresAtUtc" > clock.now_utc;
        """;

    private readonly RentalCommandDbContext _db;

    public SimWorkerCommandClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<SimWorkerCommandClaim?> ClaimOldestAsync(
        string claimOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            // Open through EF so configured connection interceptors establish the Engine's RLS
            // actor/session context before the raw one-statement claim executes.
            await _db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimSql;
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("leaseDuration", NpgsqlDbType.Interval) { Value = leaseDuration });

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? new SimWorkerCommandClaim(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3))
                : null;
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    public Task<int> MarkDoneAsync(
        Guid id, string claimOwner, Guid claimToken, string resultJson, DateTime completedAtUtc,
        CancellationToken ct = default) =>
        ExecuteMutationAsync(MarkDoneSql, id, claimOwner, claimToken, ct,
            new NpgsqlParameter("resultJson", NpgsqlDbType.Jsonb) { Value = resultJson },
            new NpgsqlParameter("completedRealUtc", NpgsqlDbType.TimestampTz) { Value = AsUtc(completedAtUtc) });

    public Task<int> MarkErrorAsync(
        Guid id, string claimOwner, Guid claimToken, string error, DateTime completedAtUtc,
        CancellationToken ct = default) =>
        ExecuteMutationAsync(MarkErrorSql, id, claimOwner, claimToken, ct,
            new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = LimitError(error) },
            new NpgsqlParameter("completedRealUtc", NpgsqlDbType.TimestampTz) { Value = AsUtc(completedAtUtc) });

    private async Task<int> ExecuteMutationAsync(
        string sql, Guid id, string claimOwner, Guid claimToken, CancellationToken ct,
        params NpgsqlParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = claimToken });
            foreach (var parameter in parameters) command.Parameters.Add(parameter);
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    private static string LimitError(string value) => value.Length <= 2000 ? value : value[..2000];

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
