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
        string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, int batchSize, CancellationToken ct = default);
    Task<int> MarkAcceptedAsync(
        long id, Guid claimToken, DateTime acceptedAtUtc, string provider, string? providerMessageId,
        CancellationToken ct = default);
    Task<int> MarkRetryableAsync(
        long id, Guid claimToken, DateTime nextAttemptAtUtc, string error, CancellationToken ct = default);
    Task<int> MarkDeadLetteredAsync(
        long id, Guid claimToken, DateTime deadLetteredAtUtc, OutboxFailureKind failureKind, string error,
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
        WITH candidates AS (
            SELECT "Id"
            FROM "OutboxMessages"
            WHERE "AcceptedAtUtc" IS NULL
              AND "DeadLetteredAtUtc" IS NULL
              AND "NextAttemptAtUtc" <= @now
              AND ("ClaimExpiresAtUtc" IS NULL OR "ClaimExpiresAtUtc" <= @now)
            ORDER BY "NextAttemptAtUtc", "CreatedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "OutboxMessages" AS message
        SET "ClaimOwner" = @claimOwner,
            "ClaimToken" = @claimToken,
            "ClaimExpiresAtUtc" = @claimExpiresAtUtc,
            "AttemptCount" = message."AttemptCount" + 1,
            "LastAttemptAtUtc" = @now,
            "FailureKind" = NULL,
            "LastError" = NULL
        FROM candidates
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

    private readonly RentalCommandDbContext _db;

    public OutboxClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<OutboxClaim>> ClaimAsync(
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

        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimSql;
            command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = AsUtc(nowUtc) });
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
            command.Parameters.Add(new NpgsqlParameter("claimExpiresAtUtc", NpgsqlDbType.TimestampTz)
            {
                Value = AsUtc(nowUtc).Add(leaseDuration),
            });
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
        DateTime acceptedAtUtc,
        string provider,
        string? providerMessageId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return Owned(id, claimToken).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.AcceptedAtUtc, AsUtc(acceptedAtUtc))
            .SetProperty(row => row.Provider, provider)
            .SetProperty(row => row.ProviderMessageId, providerMessageId)
            .SetProperty(row => row.FailureKind, (OutboxFailureKind?)null)
            .SetProperty(row => row.LastError, (string?)null)
            .SetProperty(row => row.ClaimOwner, (string?)null)
            .SetProperty(row => row.ClaimToken, (Guid?)null)
            .SetProperty(row => row.ClaimExpiresAtUtc, (DateTime?)null), ct);
    }

    public Task<int> MarkRetryableAsync(
        long id,
        Guid claimToken,
        DateTime nextAttemptAtUtc,
        string error,
        CancellationToken ct = default) =>
        Owned(id, claimToken).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.NextAttemptAtUtc, AsUtc(nextAttemptAtUtc))
            .SetProperty(row => row.FailureKind, OutboxFailureKind.Retryable)
            .SetProperty(row => row.LastError, LimitError(error))
            .SetProperty(row => row.ClaimOwner, (string?)null)
            .SetProperty(row => row.ClaimToken, (Guid?)null)
            .SetProperty(row => row.ClaimExpiresAtUtc, (DateTime?)null), ct);

    public Task<int> MarkDeadLetteredAsync(
        long id,
        Guid claimToken,
        DateTime deadLetteredAtUtc,
        OutboxFailureKind failureKind,
        string error,
        CancellationToken ct = default)
    {
        if (failureKind == OutboxFailureKind.Retryable)
            throw new ArgumentException("A dead-letter reason must be terminal.", nameof(failureKind));

        return Owned(id, claimToken).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.DeadLetteredAtUtc, AsUtc(deadLetteredAtUtc))
            .SetProperty(row => row.FailureKind, failureKind)
            .SetProperty(row => row.LastError, LimitError(error))
            .SetProperty(row => row.ClaimOwner, (string?)null)
            .SetProperty(row => row.ClaimToken, (Guid?)null)
            .SetProperty(row => row.ClaimExpiresAtUtc, (DateTime?)null), ct);
    }

    private IQueryable<Core.Entities.OutboxMessage> Owned(long id, Guid claimToken) =>
        _db.OutboxMessages.Where(row => row.Id == id && row.ClaimToken == claimToken);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string LimitError(string error) =>
        string.IsNullOrWhiteSpace(error)
            ? "Delivery failed without an error message."
            : error.Length <= 4000 ? error : error[..4000];
}
