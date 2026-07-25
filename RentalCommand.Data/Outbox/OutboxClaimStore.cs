using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Outbox;

/// <summary>A leased delivery returned by the database claim statement.</summary>
public sealed record OutboxClaim(
    long Id,
    int? PortfolioId,
    string MessageType,
    string Payload,
    string IdempotencyKey,
    int AttemptCount,
    DateTime CreatedAtUtc,
    Guid ClaimToken);

public interface IOutboxClaimStore
{
    Task<IReadOnlyList<OutboxClaim>> ClaimAsync(
        string claimOwner, TimeSpan leaseDuration, int batchSize, CancellationToken ct = default);
    Task<int> MarkAcceptedAsync(
        long id, Guid claimToken, string provider, string? providerMessageId,
        CancellationToken ct = default);
    Task<int> MarkRetryableAsync(
        long id, Guid claimToken, TimeSpan retryDelay, string error, CancellationToken ct = default);
    Task<int> MarkDeadLetteredAsync(
        long id, Guid claimToken, OutboxFailureKind failureKind, string error,
        CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL queue boundary. Claiming is one UPDATE statement backed by
/// <c>FOR UPDATE SKIP LOCKED</c>; provider calls happen only after this method returns.
/// Every completion is fenced by both message id and claim token.
/// </summary>
public sealed class OutboxClaimStore : IOutboxClaimStore
{
    private const string ClaimSql = """
        WITH clock AS MATERIALIZED (
            SELECT clock_timestamp() AS now_utc
        ), candidates AS (
            SELECT candidate."Id"
            FROM "OutboxMessages" AS candidate
            CROSS JOIN clock
            WHERE candidate."AcceptedAtUtc" IS NULL
              AND candidate."DeadLetteredAtUtc" IS NULL
              AND candidate."NextAttemptAtUtc" <= clock.now_utc
              AND (candidate."ClaimExpiresAtUtc" IS NULL OR candidate."ClaimExpiresAtUtc" <= clock.now_utc)
            ORDER BY candidate."NextAttemptAtUtc", candidate."CreatedAtUtc", candidate."Id"
            FOR UPDATE OF candidate SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "OutboxMessages" AS message
        SET "ClaimOwner" = @claimOwner,
            "ClaimToken" = gen_random_uuid(),
            "ClaimExpiresAtUtc" = clock.now_utc + @leaseDuration,
            "AttemptCount" = message."AttemptCount" + 1,
            "LastAttemptAtUtc" = clock.now_utc,
            "FailureKind" = NULL,
            "LastError" = NULL
        FROM candidates, clock
        WHERE message."Id" = candidates."Id"
        RETURNING message."Id",
                  message."PortfolioId",
                  message."MessageType",
                  message."Payload"::text,
                  message."IdempotencyKey",
                  message."AttemptCount",
                  message."CreatedAtUtc",
                  message."ClaimToken";
        """;

    private const string MarkAcceptedSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "OutboxMessages" AS message
        SET "AcceptedAtUtc" = clock.now_utc,
            "Provider" = @provider,
            "ProviderMessageId" = @providerMessageId,
            "FailureKind" = NULL,
            "LastError" = NULL,
            "ClaimOwner" = NULL,
            "ClaimToken" = NULL,
            "ClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE message."Id" = @id
          AND message."ClaimToken" = @claimToken
          AND message."ClaimExpiresAtUtc" > clock.now_utc;
        """;

    private const string MarkRetryableSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "OutboxMessages" AS message
        SET "NextAttemptAtUtc" = clock.now_utc + @retryDelay,
            "FailureKind" = @failureKind,
            "LastError" = @error,
            "ClaimOwner" = NULL,
            "ClaimToken" = NULL,
            "ClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE message."Id" = @id
          AND message."ClaimToken" = @claimToken
          AND message."ClaimExpiresAtUtc" > clock.now_utc;
        """;

    private const string MarkDeadLetteredSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "OutboxMessages" AS message
        SET "DeadLetteredAtUtc" = clock.now_utc,
            "FailureKind" = @failureKind,
            "LastError" = @error,
            "ClaimOwner" = NULL,
            "ClaimToken" = NULL,
            "ClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE message."Id" = @id
          AND message."ClaimToken" = @claimToken
          AND message."ClaimExpiresAtUtc" > clock.now_utc;
        """;

    private readonly RentalCommandDbContext _db;

    public OutboxClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<OutboxClaim>> ClaimAsync(
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

            var claims = new List<OutboxClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claims.Add(new OutboxClaim(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetDateTime(6),
                    reader.GetGuid(7)));
            }

            return claims;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    public Task<int> MarkAcceptedAsync(
        long id,
        Guid claimToken,
        string provider,
        string? providerMessageId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return ExecuteMutationAsync(MarkAcceptedSql, id, claimToken, ct,
            new NpgsqlParameter("provider", NpgsqlDbType.Text) { Value = provider },
            new NpgsqlParameter("providerMessageId", NpgsqlDbType.Text)
                { Value = providerMessageId is null ? DBNull.Value : providerMessageId });
    }

    public Task<int> MarkRetryableAsync(
        long id,
        Guid claimToken,
        TimeSpan retryDelay,
        string error,
        CancellationToken ct = default)
    {
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        return ExecuteMutationAsync(MarkRetryableSql, id, claimToken, ct,
            new NpgsqlParameter("retryDelay", NpgsqlDbType.Interval) { Value = retryDelay },
            new NpgsqlParameter("failureKind", NpgsqlDbType.Integer) { Value = (int)OutboxFailureKind.Retryable },
            new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = LimitError(error) });
    }

    public Task<int> MarkDeadLetteredAsync(
        long id,
        Guid claimToken,
        OutboxFailureKind failureKind,
        string error,
        CancellationToken ct = default)
    {
        if (failureKind == OutboxFailureKind.Retryable)
            throw new ArgumentException("A dead-letter reason must be terminal.", nameof(failureKind));

        return ExecuteMutationAsync(MarkDeadLetteredSql, id, claimToken, ct,
            new NpgsqlParameter("failureKind", NpgsqlDbType.Integer) { Value = (int)failureKind },
            new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = LimitError(error) });
    }

    private async Task<int> ExecuteMutationAsync(
        string sql, long id, Guid claimToken, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = claimToken });
            foreach (var parameter in parameters) command.Parameters.Add(parameter);
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    private static string LimitError(string error) =>
        string.IsNullOrWhiteSpace(error)
            ? "Delivery failed without an error message."
            : error.Length <= 4000 ? error : error[..4000];
}
