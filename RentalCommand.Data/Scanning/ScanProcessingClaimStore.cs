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
        string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);

    Task<int> MarkReviewingAsync(
        int id, Guid claimToken, ScanProcessingResult result, CancellationToken ct = default);

    Task<int> MarkFailedAsync(
        int id, Guid claimToken, DateTime reviewedAtUtc, string? failureReason,
        CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL boundary for paid scan extraction. Candidate selection, expiry reclaim, ordering,
/// paging, row locking, and lease assignment are one statement. LLM/blob work runs only after it
/// returns. Result and failure writes are fenced by draft id plus the current claim token.
/// </summary>
public sealed class ScanProcessingClaimStore : IScanProcessingClaimStore
{
    private const string ClaimSql = """
        WITH candidates AS (
            SELECT draft."Id"
            FROM "ScanDrafts" AS draft
            INNER JOIN "Portfolios" AS portfolio ON portfolio."Id" = draft."PortfolioId"
            WHERE portfolio."DeletedAt" IS NULL
              AND (draft."Status" = 'Pending'
                   OR (draft."Status" = 'Processing' AND draft."ProcessingClaimExpiresAtUtc" <= @now))
              AND (draft."ProcessingClaimToken" IS NULL OR draft."ProcessingClaimExpiresAtUtc" <= @now)
            ORDER BY draft."CreatedAt", draft."Id"
            FOR UPDATE OF draft SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "ScanDrafts" AS draft
        SET "Status" = 'Processing',
            "ProcessingClaimOwner" = @claimOwner,
            "ProcessingClaimToken" = @claimToken,
            "ProcessingClaimExpiresAtUtc" = @claimExpiresAtUtc,
            "ProcessingAttemptCount" = draft."ProcessingAttemptCount" + 1,
            "ProcessingLastAttemptAtUtc" = @now,
            "FailureReason" = NULL
        FROM candidates
        WHERE draft."Id" = candidates."Id"
        RETURNING draft."Id", draft."PortfolioId", draft."FilePath",
                  draft."SourceStoredFileId", draft."TargetEntityType", draft."ProcessingClaimToken";
        """;

    private readonly RentalCommandDbContext _db;

    public ScanProcessingClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<ScanProcessingClaim>> ClaimAsync(
        string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));

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
                    reader.GetGuid(5)));
            }

            return claims;
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    public Task<int> MarkReviewingAsync(
        int id, Guid claimToken, ScanProcessingResult result, CancellationToken ct = default) =>
        Owned(id, claimToken).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.ExtractedFields, result.ExtractedFields)
            .SetProperty(row => row.FailureReason, (string?)null)
            .SetProperty(row => row.ModelId, result.ModelId)
            .SetProperty(row => row.TokensUsed, result.TokensUsed)
            .SetProperty(row => row.CostUsd, result.CostUsd)
            .SetProperty(row => row.TargetEntityType, result.TargetEntityType)
            .SetProperty(row => row.Status, "Reviewing")
            .SetProperty(row => row.ReviewedAt, AsUtc(result.ReviewedAtUtc))
            .SetProperty(row => row.ProcessingClaimOwner, (string?)null)
            .SetProperty(row => row.ProcessingClaimToken, (Guid?)null)
            .SetProperty(row => row.ProcessingClaimExpiresAtUtc, (DateTime?)null), ct);

    public Task<int> MarkFailedAsync(
        int id, Guid claimToken, DateTime reviewedAtUtc, string? failureReason,
        CancellationToken ct = default) =>
        Owned(id, claimToken).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Status, "Failed")
            .SetProperty(row => row.FailureReason, LimitError(failureReason))
            .SetProperty(row => row.ReviewedAt, AsUtc(reviewedAtUtc))
            .SetProperty(row => row.ProcessingClaimOwner, (string?)null)
            .SetProperty(row => row.ProcessingClaimToken, (Guid?)null)
            .SetProperty(row => row.ProcessingClaimExpiresAtUtc, (DateTime?)null), ct);

    private IQueryable<Core.Entities.ScanDraft> Owned(int id, Guid claimToken) =>
        _db.ScanDrafts.Where(row =>
            row.Id == id && row.Status == "Processing" && row.ProcessingClaimToken == claimToken);

    private static string? LimitError(string? value) => value is null || value.Length <= 500 ? value : value[..500];

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
