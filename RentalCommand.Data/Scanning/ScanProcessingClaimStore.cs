using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace RentalCommand.Data.Scanning;

public sealed record ScanProcessingClaim(
    int Id,
    int PortfolioId,
    string FilePath,
    int? SourceStoredFileId,
    string TargetEntityType,
    string ClaimOwner,
    Guid ClaimToken);

public sealed record ScanProcessingResult(
    string ExtractedFields,
    string? ModelId,
    int? TokensUsed,
    decimal? CostUsd,
    string TargetEntityType,
    DateTime ReviewedAtUtc);

public interface IScanProcessingClaimStore
{
    Task<IReadOnlyList<ScanProcessingClaim>> ClaimAsync(
        string claimOwner, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);

    Task<int> MarkReviewingAsync(
        int id, string claimOwner, Guid claimToken, ScanProcessingResult result, CancellationToken ct = default);

    Task<int> MarkFailedAsync(
        int id, string claimOwner, Guid claimToken, DateTime reviewedAtUtc, string? failureReason,
        CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL boundary for paid scan extraction. Candidate selection, expiry reclaim, ordering,
/// paging, row locking, and lease assignment are one statement. LLM/blob work runs only after it
/// returns. Result and failure writes require the current owner, token, and a live DB-clock lease.
/// </summary>
public sealed class ScanProcessingClaimStore : IScanProcessingClaimStore
{
    private const string ClaimSql = """
        WITH clock AS MATERIALIZED (
            SELECT clock_timestamp() AS now_utc
        ), candidates AS (
            SELECT draft."Id"
            FROM "ScanDrafts" AS draft
            INNER JOIN "Portfolios" AS portfolio ON portfolio."Id" = draft."PortfolioId"
            CROSS JOIN clock
            WHERE portfolio."DeletedAt" IS NULL
              AND (draft."Status" = 'Pending'
                   OR (draft."Status" = 'Processing' AND draft."ProcessingClaimExpiresAtUtc" <= clock.now_utc))
              AND (draft."ProcessingClaimToken" IS NULL OR draft."ProcessingClaimExpiresAtUtc" <= clock.now_utc)
            ORDER BY draft."CreatedAt", draft."Id"
            FOR UPDATE OF draft SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "ScanDrafts" AS draft
        SET "Status" = 'Processing',
            "ProcessingClaimOwner" = @claimOwner,
            "ProcessingClaimToken" = gen_random_uuid(),
            "ProcessingClaimExpiresAtUtc" = clock.now_utc + @leaseDuration,
            "ProcessingAttemptCount" = draft."ProcessingAttemptCount" + 1,
            "ProcessingLastAttemptAtUtc" = clock.now_utc,
            "FailureReason" = NULL
        FROM candidates, clock
        WHERE draft."Id" = candidates."Id"
        RETURNING draft."Id", draft."PortfolioId", draft."FilePath",
                  draft."SourceStoredFileId", draft."TargetEntityType",
                  draft."ProcessingClaimOwner", draft."ProcessingClaimToken";
        """;

    private const string MarkReviewingSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "ScanDrafts" AS draft
        SET "ExtractedFields" = CAST(@extractedFields AS jsonb),
            "FailureReason" = NULL,
            "ModelId" = @modelId,
            "TokensUsed" = @tokensUsed,
            "CostUsd" = @costUsd,
            "TargetEntityType" = @targetEntityType,
            "Status" = 'Reviewing',
            "ReviewedAt" = @reviewedAtUtc,
            "ProcessingClaimOwner" = NULL,
            "ProcessingClaimToken" = NULL,
            "ProcessingClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE draft."Id" = @id
          AND draft."Status" = 'Processing'
          AND draft."ProcessingClaimOwner" = @claimOwner
          AND draft."ProcessingClaimToken" = @claimToken
          AND draft."ProcessingClaimExpiresAtUtc" > clock.now_utc;
        """;

    private const string MarkFailedSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "ScanDrafts" AS draft
        SET "Status" = 'Failed',
            "FailureReason" = @failureReason,
            "ReviewedAt" = @reviewedAtUtc,
            "ProcessingClaimOwner" = NULL,
            "ProcessingClaimToken" = NULL,
            "ProcessingClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE draft."Id" = @id
          AND draft."Status" = 'Processing'
          AND draft."ProcessingClaimOwner" = @claimOwner
          AND draft."ProcessingClaimToken" = @claimToken
          AND draft."ProcessingClaimExpiresAtUtc" > clock.now_utc;
        """;

    private readonly RentalCommandDbContext _db;

    public ScanProcessingClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<ScanProcessingClaim>> ClaimAsync(
        string claimOwner, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));

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
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });

            var claims = new List<ScanProcessingClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claims.Add(new ScanProcessingClaim(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetGuid(6)));
            }

            return claims;
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    public Task<int> MarkReviewingAsync(
        int id, string claimOwner, Guid claimToken, ScanProcessingResult result,
        CancellationToken ct = default) =>
        ExecuteMutationAsync(MarkReviewingSql, id, claimOwner, claimToken, ct,
            new NpgsqlParameter("extractedFields", NpgsqlDbType.Jsonb) { Value = result.ExtractedFields },
            Nullable("modelId", NpgsqlDbType.Text, result.ModelId),
            Nullable("tokensUsed", NpgsqlDbType.Integer, result.TokensUsed),
            Nullable("costUsd", NpgsqlDbType.Numeric, result.CostUsd),
            new NpgsqlParameter("targetEntityType", NpgsqlDbType.Text) { Value = result.TargetEntityType },
            new NpgsqlParameter("reviewedAtUtc", NpgsqlDbType.TimestampTz) { Value = AsUtc(result.ReviewedAtUtc) });

    public Task<int> MarkFailedAsync(
        int id, string claimOwner, Guid claimToken, DateTime reviewedAtUtc, string? failureReason,
        CancellationToken ct = default) =>
        ExecuteMutationAsync(MarkFailedSql, id, claimOwner, claimToken, ct,
            Nullable("failureReason", NpgsqlDbType.Text, LimitError(failureReason)),
            new NpgsqlParameter("reviewedAtUtc", NpgsqlDbType.TimestampTz) { Value = AsUtc(reviewedAtUtc) });

    private async Task<int> ExecuteMutationAsync(
        string sql, int id, string claimOwner, Guid claimToken, CancellationToken ct,
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
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = id });
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

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static string? LimitError(string? value) => value is null || value.Length <= 500 ? value : value[..500];

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
