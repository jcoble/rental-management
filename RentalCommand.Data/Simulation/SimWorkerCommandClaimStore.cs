using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Simulation;

public sealed record SimWorkerCommandClaim(Guid Id, string WorkerKey, Guid ClaimToken);

public interface ISimWorkerCommandClaimStore
{
    Task<SimWorkerCommandClaim?> ClaimOldestAsync(
        string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken ct = default);
    Task<int> MarkDoneAsync(
        Guid id, Guid claimToken, string resultJson, DateTime completedAtUtc, CancellationToken ct = default);
    Task<int> MarkErrorAsync(
        Guid id, Guid claimToken, string error, DateTime completedAtUtc, CancellationToken ct = default);
}

/// <summary>One-statement leased simulation queue with token-fenced terminal updates.</summary>
public sealed class SimWorkerCommandClaimStore : ISimWorkerCommandClaimStore
{
    private const string ClaimSql = """
        WITH candidate AS (
            SELECT "Id"
            FROM "SimWorkerCommands"
            WHERE ("Status" = 'Pending'
                   OR ("Status" = 'Running' AND "ClaimExpiresAtUtc" <= @now))
              AND ("ClaimToken" IS NULL OR "ClaimExpiresAtUtc" <= @now)
            ORDER BY "CreatedRealUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
        )
        UPDATE "SimWorkerCommands" AS command
        SET "Status" = 'Running',
            "ClaimOwner" = @claimOwner,
            "ClaimToken" = @claimToken,
            "ClaimExpiresAtUtc" = @claimExpiresAtUtc,
            "AttemptCount" = command."AttemptCount" + 1,
            "LastAttemptAtUtc" = @now,
            "Error" = NULL
        FROM candidate
        WHERE command."Id" = candidate."Id"
        RETURNING command."Id", command."WorkerKey", command."ClaimToken";
        """;

    private readonly RentalCommandDbContext _db;

    public SimWorkerCommandClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<SimWorkerCommandClaim?> ClaimOldestAsync(
        string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var now = AsUtc(nowUtc);
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
            command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
            command.Parameters.Add(new NpgsqlParameter("claimExpiresAtUtc", NpgsqlDbType.TimestampTz)
            {
                Value = now.Add(leaseDuration),
            });

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? new SimWorkerCommandClaim(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2))
                : null;
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    public Task<int> MarkDoneAsync(
        Guid id, Guid claimToken, string resultJson, DateTime completedAtUtc, CancellationToken ct = default) =>
        Owned(id, claimToken).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Status, SimWorkerCommandStatus.Done)
            .SetProperty(row => row.ResultJson, resultJson)
            .SetProperty(row => row.Error, (string?)null)
            .SetProperty(row => row.CompletedRealUtc, AsUtc(completedAtUtc))
            .SetProperty(row => row.ClaimOwner, (string?)null)
            .SetProperty(row => row.ClaimToken, (Guid?)null)
            .SetProperty(row => row.ClaimExpiresAtUtc, (DateTime?)null), ct);

    public Task<int> MarkErrorAsync(
        Guid id, Guid claimToken, string error, DateTime completedAtUtc, CancellationToken ct = default) =>
        Owned(id, claimToken).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Status, SimWorkerCommandStatus.Error)
            .SetProperty(row => row.Error, LimitError(error))
            .SetProperty(row => row.CompletedRealUtc, AsUtc(completedAtUtc))
            .SetProperty(row => row.ClaimOwner, (string?)null)
            .SetProperty(row => row.ClaimToken, (Guid?)null)
            .SetProperty(row => row.ClaimExpiresAtUtc, (DateTime?)null), ct);

    private IQueryable<SimWorkerCommand> Owned(Guid id, Guid claimToken) =>
        _db.SimWorkerCommands.Where(row =>
            row.Id == id && row.Status == SimWorkerCommandStatus.Running && row.ClaimToken == claimToken);

    private static string LimitError(string value) => value.Length <= 2000 ? value : value[..2000];

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
