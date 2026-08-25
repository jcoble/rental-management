using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Simulation;

public sealed record SimWorkerCommandClaim(Guid Id, string WorkerKey, string ClaimOwner, Guid ClaimToken);

public sealed record SimWorkerTerminalCommand(
    Guid CommandId,
    string ClaimOwner,
    Guid ClaimToken,
    string Status,
    DateTime CompletedRealUtc,
    string? ResultJson = null,
    string? Error = null) : IAtomicCommandData;

public sealed record SimWorkerTerminalResult(bool Applied, Guid CommandId, string Status);

public interface ISimWorkerCommandClaimStore
{
    Task<SimWorkerCommandClaim?> ClaimOldestAsync(
        string claimOwner, TimeSpan leaseDuration, CancellationToken ct = default);
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

}

public static class SimWorkerTerminalWrite
{
    public const string ResultContract = "sim-worker.terminal.v1";
    private const string OperationName = "sim-worker.terminal";

    public static string StepKey(SimWorkerTerminalCommand command) =>
        $"sim-worker-terminal:{command.CommandId:N}:{command.ClaimToken:N}:{command.Status.ToLowerInvariant()}";

    public static TransactionalWrite<SimWorkerTerminalCommand, SimWorkerTerminalResult> Write(
        RentalCommandDbContext db,
        SimWorkerTerminalCommand command)
    {
        Validate(command);
        return new TransactionalWrite<SimWorkerTerminalCommand, SimWorkerTerminalResult>(
            OperationName,

            command,
            ResultContract,
            WriteLockPlan.None,
            (request, context, ct) => ExecuteAsync(db, request, context, ct),
            static (_, _, _) => Task.CompletedTask);
    }

    private static async Task<SimWorkerTerminalResult> ExecuteAsync(
        RentalCommandDbContext db,
        SimWorkerTerminalCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("SimWorkerCommand", command.CommandId, ct);
        var rows = await db.ExecuteAtomicSqlMutationAsync<int>(
            context,
            command.Status == SimWorkerCommandStatus.Done ? DoneSql(command) : ErrorSql(command),
            [new AtomicSqlMutationTarget("SimWorkerCommands", AtomicSqlMutationOperation.Update)],
            ct);
        return new SimWorkerTerminalResult(rows.Count == 1, command.CommandId, command.Status);
    }

    private static void Validate(SimWorkerTerminalCommand command)
    {
        if (command.CommandId == Guid.Empty || command.ClaimToken == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ClaimOwner);
        if (command.CompletedRealUtc == default
            || command.Status == SimWorkerCommandStatus.Done && string.IsNullOrWhiteSpace(command.ResultJson)
            || command.Status == SimWorkerCommandStatus.Done && command.Error is not null
            || command.Status == SimWorkerCommandStatus.Error && command.ResultJson is not null
            || command.Status == SimWorkerCommandStatus.Error && string.IsNullOrWhiteSpace(command.Error)
            || command.Status is not (SimWorkerCommandStatus.Done or SimWorkerCommandStatus.Error))
        {
            throw new ArgumentException("A valid simulation-worker terminal transition is required.", nameof(command));
        }
    }

    private static string LimitError(string value) => value.Length <= 2000 ? value : value[..2000];

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static FormattableString DoneSql(SimWorkerTerminalCommand command) => $"""
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "SimWorkerCommands" AS worker_command
        SET "Status" = 'Done', "ResultJson" = CAST({command.ResultJson} AS jsonb), "Error" = NULL,
            "CompletedRealUtc" = {AsUtc(command.CompletedRealUtc)},
            "ClaimOwner" = NULL, "ClaimToken" = NULL, "ClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE worker_command."Id" = {command.CommandId} AND worker_command."Status" = 'Running'
          AND worker_command."ClaimOwner" = {command.ClaimOwner}
          AND worker_command."ClaimToken" = {command.ClaimToken}
          AND worker_command."ClaimExpiresAtUtc" > clock.now_utc
        RETURNING 1 AS "Value";
        """;

    private static FormattableString ErrorSql(SimWorkerTerminalCommand command) => $"""
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "SimWorkerCommands" AS worker_command
        SET "Status" = 'Error', "Error" = {LimitError(command.Error!)},
            "CompletedRealUtc" = {AsUtc(command.CompletedRealUtc)},
            "ClaimOwner" = NULL, "ClaimToken" = NULL, "ClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE worker_command."Id" = {command.CommandId} AND worker_command."Status" = 'Running'
          AND worker_command."ClaimOwner" = {command.ClaimOwner}
          AND worker_command."ClaimToken" = {command.ClaimToken}
          AND worker_command."ClaimExpiresAtUtc" > clock.now_utc
        RETURNING 1 AS "Value";
        """;
}
