using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Data.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Documents;

public sealed class UploadOperationConflictException : InvalidOperationException
{
    public UploadOperationConflictException(string operationId)
        : base($"Upload operation '{operationId}' was already used for a different request.") { }
}

public sealed record PendingFileUploadAdmission(
    Guid Id,
    string StoragePath,
    PendingFileUploadState State,
    int? StoredFileId,
    string RequestFingerprint,
    bool CleanupClaimed = false);

public sealed record PendingFileUploadCleanupClaim(
    Guid Id, string ClaimOwner, Guid ClaimToken, string StoragePath);

public interface IPendingFileUploadStore
{
    Task<PendingFileUploadAdmission> PrepareAsync(
        int portfolioId,
        int actorScopeId,
        string purpose,
        string clientOperationId,
        string requestFingerprint,
        string fileName,
        string contentType,
        long sizeBytes,
        DateTime nowUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<PendingFileUploadCleanupClaim>> ClaimExpiredAsync(
        string claimOwner,
        TimeSpan preparedRetention,
        TimeSpan claimLease,
        int batchSize,
        CancellationToken ct = default);

    Task<int> MarkAbandonedAsync(
        Guid id, string claimOwner, Guid claimToken, CancellationToken ct = default);
    Task<int> ReleaseCleanupClaimAsync(
        Guid id, string claimOwner, Guid claimToken, CancellationToken ct = default);
}

public sealed class PendingFileUploadStore : IPendingFileUploadStore
{
    private readonly RentalCommandDbContext _db;
    private readonly IInternalSetBasedWriteScope _writeScope;

    internal PendingFileUploadStore(
        RentalCommandDbContext db,
        IInternalSetBasedWriteScope writeScope)
    {
        _db = db;
        _writeScope = writeScope;
    }

    public async Task<PendingFileUploadAdmission> PrepareAsync(
        int portfolioId,
        int actorScopeId,
        string purpose,
        string clientOperationId,
        string requestFingerprint,
        string fileName,
        string contentType,
        long sizeBytes,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        var normalizedPurpose = purpose.Trim().ToLowerInvariant();
        var normalizedOperationId = clientOperationId.Trim();
        var operationHash = ComputeOperationKeyHash(normalizedOperationId);
        var id = Guid.NewGuid();
        var safeName = SanitizeFileName(fileName);
        var storagePath = $"pending-{id:N}-{safeName}";
        // The argument remains part of the interface for callers that also stamp business-time
        // content, but pending-upload lifecycle timestamps are operational data and must come from
        // PostgreSQL's wall clock. A simulation timestamp can be older than the cleanup TTL.
        _ = nowUtc;

        using var admissionLease = _writeScope.BeginWrite("PendingFileUploads", InternalWriteOperation.Insert);
        var rows = await _db.Database.SqlQuery<PendingFileUploadAdmissionRow>($$"""
            WITH clock AS MATERIALIZED (
              SELECT clock_timestamp() AS now_utc
            ), inserted AS (
              INSERT INTO "PendingFileUploads"
                ("Id", "PortfolioId", "ActorScopeId", "Purpose", "OperationKeyHash",
                 "RequestFingerprint", "StoragePath", "FileName", "ContentType", "SizeBytes",
                 "State", "CreatedAtUtc", "UpdatedAtUtc")
              VALUES
                ({{id}}, {{portfolioId}}, {{actorScopeId}}, {{normalizedPurpose}}, {{operationHash}},
                 {{requestFingerprint}}, {{storagePath}}, {{safeName}}, {{contentType}}, {{sizeBytes}},
                 {{(int)PendingFileUploadState.Prepared}},
                 (SELECT now_utc FROM clock), (SELECT now_utc FROM clock))
              ON CONFLICT ("PortfolioId", "ActorScopeId", "Purpose", "OperationKeyHash")
              DO UPDATE SET "OperationKeyHash" = EXCLUDED."OperationKeyHash"
              RETURNING "Id", "StoragePath", "State", "StoredFileId", "RequestFingerprint"
            )
            SELECT inserted."Id",
                   inserted."StoragePath",
                   inserted."State",
                   inserted."StoredFileId",
                   inserted."RequestFingerprint",
                   NULL::uuid AS "CleanupClaimToken"
            FROM inserted
            UNION ALL
            SELECT upload."Id",
                   upload."StoragePath",
                   upload."State",
                   upload."StoredFileId",
                   upload."RequestFingerprint",
                   upload."CleanupClaimToken"
            FROM "PendingFileUploads" AS upload
            WHERE upload."PortfolioId" = {{portfolioId}}
              AND upload."ActorScopeId" = {{actorScopeId}}
              AND upload."Purpose" = {{normalizedPurpose}}
              AND upload."OperationKeyHash" = {{operationHash}}
              AND NOT EXISTS (SELECT 1 FROM inserted)
            LIMIT 1
            """).ToListAsync(ct);
        var row = rows.Single();
        var admission = new PendingFileUploadAdmission(
            row.Id,
            row.StoragePath,
            (PendingFileUploadState)row.State,
            row.StoredFileId,
            row.RequestFingerprint,
            row.CleanupClaimToken.HasValue);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(admission.RequestFingerprint),
                Encoding.UTF8.GetBytes(requestFingerprint)))
        {
            throw new UploadOperationConflictException(normalizedOperationId);
        }

        return admission;
    }

    public async Task<IReadOnlyList<PendingFileUploadCleanupClaim>> ClaimExpiredAsync(
        string claimOwner,
        TimeSpan preparedRetention,
        TimeSpan claimLease,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (preparedRetention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(preparedRetention));
        if (claimLease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(claimLease));
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        using var lease = _writeScope.BeginWrite("PendingFileUploads", InternalWriteOperation.Update);
        var rows = await _db.Database.SqlQueryRaw<PendingFileUploadCleanupClaimRow>("""
            WITH clock AS MATERIALIZED (
                SELECT clock_timestamp() AS now_utc
            ), candidates AS (
                SELECT upload."Id"
                FROM "PendingFileUploads" AS upload
                CROSS JOIN clock
                WHERE upload."State" = @prepared
                  AND upload."CreatedAtUtc" <= clock.now_utc - @preparedRetention
                  AND (upload."CleanupClaimToken" IS NULL
                       OR upload."CleanupClaimExpiresAtUtc" <= clock.now_utc)
                ORDER BY upload."CreatedAtUtc", upload."Id"
                FOR UPDATE OF upload SKIP LOCKED
                LIMIT @batchSize
            )
            UPDATE "PendingFileUploads" AS upload
            SET "CleanupClaimOwner" = @claimOwner,
                "CleanupClaimToken" = gen_random_uuid(),
                "CleanupClaimExpiresAtUtc" = clock.now_utc + @claimLease,
                "UpdatedAtUtc" = clock.now_utc
            FROM candidates, clock
            WHERE upload."Id" = candidates."Id"
            RETURNING upload."Id",
                      upload."CleanupClaimOwner" AS "ClaimOwner",
                      upload."CleanupClaimToken" AS "ClaimToken",
                      upload."StoragePath";
            """,
            new NpgsqlParameter("prepared", NpgsqlDbType.Integer) { Value = (int)PendingFileUploadState.Prepared },
            new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner },
            new NpgsqlParameter("preparedRetention", NpgsqlDbType.Interval) { Value = preparedRetention },
            new NpgsqlParameter("claimLease", NpgsqlDbType.Interval) { Value = claimLease },
            new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize })
            .ToListAsync(ct);

        return rows
            .Select(row => new PendingFileUploadCleanupClaim(row.Id, row.ClaimOwner, row.ClaimToken, row.StoragePath))
            .ToArray();
    }

    public Task<int> MarkAbandonedAsync(
        Guid id, string claimOwner, Guid claimToken, CancellationToken ct = default) =>
        ExecuteCleanupMutationAsync("""
            WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
            UPDATE "PendingFileUploads" AS upload
            SET "State" = @abandoned,
                "UpdatedAtUtc" = clock.now_utc,
                "CleanupClaimOwner" = NULL,
                "CleanupClaimToken" = NULL,
                "CleanupClaimExpiresAtUtc" = NULL
            FROM clock
            WHERE upload."Id" = @id
              AND upload."State" = @prepared
              AND upload."CleanupClaimOwner" = @claimOwner
              AND upload."CleanupClaimToken" = @claimToken
              AND upload."CleanupClaimExpiresAtUtc" > clock.now_utc;
            """, id, claimOwner, claimToken, ct,
            new NpgsqlParameter("abandoned", NpgsqlDbType.Integer)
                { Value = (int)PendingFileUploadState.Abandoned });

    public Task<int> ReleaseCleanupClaimAsync(
        Guid id, string claimOwner, Guid claimToken, CancellationToken ct = default) =>
        ExecuteCleanupMutationAsync("""
            WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
            UPDATE "PendingFileUploads" AS upload
            SET "CleanupClaimOwner" = NULL,
                "CleanupClaimToken" = NULL,
                "CleanupClaimExpiresAtUtc" = NULL,
                "UpdatedAtUtc" = clock.now_utc
            FROM clock
            WHERE upload."Id" = @id
              AND upload."State" = @prepared
              AND upload."CleanupClaimOwner" = @claimOwner
              AND upload."CleanupClaimToken" = @claimToken
              AND upload."CleanupClaimExpiresAtUtc" > clock.now_utc;
            """, id, claimOwner, claimToken, ct);

    private async Task<int> ExecuteCleanupMutationAsync(
        string sql, Guid id, string claimOwner, Guid claimToken, CancellationToken ct,
        params NpgsqlParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        using var lease = _writeScope.BeginWrite("PendingFileUploads", InternalWriteOperation.Update);
        var sqlParameters = new object[]
        {
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id },
            new NpgsqlParameter("prepared", NpgsqlDbType.Integer)
                { Value = (int)PendingFileUploadState.Prepared },
            new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner },
            new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = claimToken },
        }.Concat(parameters).ToArray();
        return await _db.Database.ExecuteSqlRawAsync(
            sql,
            sqlParameters,
            ct);
    }

    private sealed class PendingFileUploadCleanupClaimRow
    {
        public Guid Id { get; init; }
        public string ClaimOwner { get; init; } = string.Empty;
        public Guid ClaimToken { get; init; }
        public string StoragePath { get; init; } = string.Empty;
    }

    private sealed class PendingFileUploadAdmissionRow
    {
        public Guid Id { get; init; }
        public string StoragePath { get; init; } = string.Empty;
        public int State { get; init; }
        public int? StoredFileId { get; init; }
        public string RequestFingerprint { get; init; } = string.Empty;
        public Guid? CleanupClaimToken { get; init; }
    }

    public static string ComputeOperationKeyHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        var safe = Path.GetFileName(value);
        foreach (var character in Path.GetInvalidFileNameChars()) safe = safe.Replace(character, '_');
        safe = safe.Replace("..", "_", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(safe) ? "file" : safe;
    }

}
