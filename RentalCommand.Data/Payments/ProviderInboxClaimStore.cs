using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace RentalCommand.Data.Payments;

public sealed record ProviderInboxClaim(long Id, string ClaimOwner, Guid ClaimToken, int AttemptCount);

public interface IProviderInboxClaimStore
{
    Task<IReadOnlyList<ProviderInboxClaim>> ClaimAsync(
        string claimOwner,
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
        WITH claim_clock AS MATERIALIZED (
            SELECT clock_timestamp() AS now_utc
        ), candidates AS MATERIALIZED (
            SELECT candidate."Id", candidate."NextAttemptAtUtc", candidate."ReceivedAtUtc"
            FROM "ProviderInboxEvents" AS candidate, claim_clock
            WHERE candidate."ProcessedAtUtc" IS NULL
              AND candidate."DeadLetteredAtUtc" IS NULL
              AND candidate."NextAttemptAtUtc" <= claim_clock.now_utc
              AND (candidate."ClaimToken" IS NULL OR candidate."ClaimExpiresAtUtc" <= claim_clock.now_utc)
            ORDER BY candidate."NextAttemptAtUtc", candidate."ReceivedAtUtc", candidate."Id"
            LIMIT @batchSize
            FOR UPDATE OF candidate SKIP LOCKED
        ), claimed AS (
            UPDATE "ProviderInboxEvents" AS inbox
            SET "ClaimOwner" = @claimOwner,
                "ClaimToken" = gen_random_uuid(),
                "ClaimExpiresAtUtc" = claim_clock.now_utc + @leaseDuration,
                "AttemptCount" = inbox."AttemptCount" + 1,
                "LastAttemptAtUtc" = claim_clock.now_utc,
                "FailureKind" = NULL,
                "LastError" = NULL
            FROM candidates, claim_clock
            WHERE inbox."Id" = candidates."Id"
            RETURNING inbox."Id", inbox."ClaimOwner", inbox."ClaimToken", inbox."AttemptCount"
        )
        SELECT claimed."Id", claimed."ClaimOwner", claimed."ClaimToken", claimed."AttemptCount"
        FROM claimed
        JOIN candidates ON candidates."Id" = claimed."Id"
        ORDER BY candidates."NextAttemptAtUtc", candidates."ReceivedAtUtc", candidates."Id";
        """;

    private readonly RentalCommandDbContext _db;

    public ProviderInboxClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProviderInboxClaim>> ClaimAsync(
        string claimOwner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (batchSize is <= 0 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimSql;
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("leaseDuration", NpgsqlDbType.Interval) { Value = leaseDuration });
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });

            var claims = new List<ProviderInboxClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claims.Add(new ProviderInboxClaim(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetGuid(2),
                    reader.GetInt32(3)));
            }

            return claims;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }
}
