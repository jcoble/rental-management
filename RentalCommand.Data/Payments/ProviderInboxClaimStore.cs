using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace RentalCommand.Data.Payments;

public sealed record ProviderInboxClaim(long Id, Guid ClaimToken, int AttemptCount);

public interface IProviderInboxClaimStore
{
    Task<IReadOnlyList<ProviderInboxClaim>> ClaimAsync(
        string claimOwner,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL queue boundary for verified provider events. Eligibility, ordering, paging, row
/// locking, lease assignment, and attempt increment are one statement. The returned claims are
/// processed only after that statement has completed and its implicit transaction is closed.
/// </summary>
public sealed class ProviderInboxClaimStore : IProviderInboxClaimStore
{
    private const string ClaimSql = """
        WITH candidates AS (
            SELECT "Id"
            FROM "ProviderInboxEvents"
            WHERE "ProcessedAtUtc" IS NULL
              AND "DeadLetteredAtUtc" IS NULL
              AND "NextAttemptAtUtc" <= @now
              AND ("ClaimToken" IS NULL OR "ClaimExpiresAtUtc" <= @now)
            ORDER BY "NextAttemptAtUtc", "ReceivedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "ProviderInboxEvents" AS inbox
        SET "ClaimOwner" = @claimOwner,
            "ClaimToken" = @claimToken,
            "ClaimExpiresAtUtc" = @claimExpiresAtUtc,
            "AttemptCount" = inbox."AttemptCount" + 1,
            "LastAttemptAtUtc" = @now,
            "FailureKind" = NULL,
            "LastError" = NULL
        FROM candidates
        WHERE inbox."Id" = candidates."Id"
        RETURNING inbox."Id", inbox."ClaimToken", inbox."AttemptCount";
        """;

    private readonly RentalCommandDbContext _db;

    public ProviderInboxClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProviderInboxClaim>> ClaimAsync(
        string claimOwner,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (batchSize is <= 0 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var now = AsUtc(nowUtc);
        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);

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
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });

            var claims = new List<ProviderInboxClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claims.Add(new ProviderInboxClaim(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2)));
            }

            return claims;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
