using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
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
    string RequestFingerprint);

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
    private readonly IAtomicInfrastructureWriteGate _writeGate;

    public PendingFileUploadStore(
        RentalCommandDbContext db,
        IAtomicInfrastructureWriteGate writeGate)
    {
        _db = db;
        _writeGate = writeGate;
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
        var now = AsUtc(nowUtc);

        using var admissionLease = _writeGate.BeginPendingFileUploadAdmission();
        await _db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "PendingFileUploads"
                ("Id", "PortfolioId", "ActorScopeId", "Purpose", "OperationKeyHash",
                 "RequestFingerprint", "StoragePath", "FileName", "ContentType", "SizeBytes",
                 "State", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES
                ({{id}}, {{portfolioId}}, {{actorScopeId}}, {{normalizedPurpose}}, {{operationHash}},
                 {{requestFingerprint}}, {{storagePath}}, {{safeName}}, {{contentType}}, {{sizeBytes}},
                 {{(int)PendingFileUploadState.Prepared}}, {{now}}, {{now}})
            ON CONFLICT ("PortfolioId", "ActorScopeId", "Purpose", "OperationKeyHash") DO NOTHING
            """, ct);

        var admission = await _db.PendingFileUploads.AsNoTracking()
            .Where(upload => upload.PortfolioId == portfolioId
                && upload.ActorScopeId == actorScopeId
                && upload.Purpose == normalizedPurpose
                && upload.OperationKeyHash == operationHash)
            .Select(upload => new PendingFileUploadAdmission(
                upload.Id,
                upload.StoragePath,
                upload.State,
                upload.StoredFileId,
                upload.RequestFingerprint))
            .SingleAsync(ct);

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
        var connection = _db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
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
                RETURNING upload."Id", upload."CleanupClaimOwner",
                          upload."CleanupClaimToken", upload."StoragePath";
                """;
            command.Parameters.Add(new NpgsqlParameter("prepared", NpgsqlDbType.Integer) { Value = (int)PendingFileUploadState.Prepared });
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("preparedRetention", NpgsqlDbType.Interval) { Value = preparedRetention });
            command.Parameters.Add(new NpgsqlParameter("claimLease", NpgsqlDbType.Interval) { Value = claimLease });
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });
            var claims = new List<PendingFileUploadCleanupClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claims.Add(new PendingFileUploadCleanupClaim(
                    reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3)));
            }
            return claims;
        }
        finally
        {
            if (close) await _db.Database.CloseConnectionAsync();
        }
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
        var connection = _db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("prepared", NpgsqlDbType.Integer)
                { Value = (int)PendingFileUploadState.Prepared });
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = claimToken });
            foreach (var parameter in parameters) command.Parameters.Add(parameter);
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (close) await _db.Database.CloseConnectionAsync();
        }
    }

    internal static string ComputeOperationKeyHash(string value)
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

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
