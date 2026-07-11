using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
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

public sealed record PendingFileUploadCleanupClaim(Guid Id, Guid ClaimToken, string StoragePath);

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
        DateTime nowUtc,
        DateTime preparedBeforeUtc,
        TimeSpan claimLease,
        int batchSize,
        CancellationToken ct = default);

    Task<int> MarkAbandonedAsync(Guid id, Guid claimToken, DateTime nowUtc, CancellationToken ct = default);
    Task<int> ReleaseCleanupClaimAsync(Guid id, Guid claimToken, CancellationToken ct = default);
}

public sealed class PendingFileUploadStore : IPendingFileUploadStore
{
    private readonly RentalCommandDbContext _db;

    public PendingFileUploadStore(RentalCommandDbContext db) => _db = db;

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
        var operationHash = Digest(normalizedOperationId);
        var id = Guid.NewGuid();
        var safeName = SanitizeFileName(fileName);
        var storagePath = $"pending-{id:N}-{safeName}";
        var now = AsUtc(nowUtc);

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
        DateTime nowUtc,
        DateTime preparedBeforeUtc,
        TimeSpan claimLease,
        int batchSize,
        CancellationToken ct = default)
    {
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var now = AsUtc(nowUtc);
        var preparedBefore = AsUtc(preparedBeforeUtc);
        var token = Guid.NewGuid();
        var connection = _db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH candidates AS (
                    SELECT upload."Id"
                    FROM "PendingFileUploads" AS upload
                    WHERE upload."State" = @prepared
                      AND upload."CreatedAtUtc" <= @preparedBefore
                      AND (upload."CleanupClaimToken" IS NULL OR upload."CleanupClaimExpiresAtUtc" <= @now)
                    ORDER BY upload."CreatedAtUtc", upload."Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT @batchSize
                )
                UPDATE "PendingFileUploads" AS upload
                SET "CleanupClaimToken" = @token,
                    "CleanupClaimExpiresAtUtc" = @claimExpires,
                    "UpdatedAtUtc" = @now
                FROM candidates
                WHERE upload."Id" = candidates."Id"
                RETURNING upload."Id", upload."CleanupClaimToken", upload."StoragePath";
                """;
            command.Parameters.Add(new NpgsqlParameter("prepared", NpgsqlDbType.Integer) { Value = (int)PendingFileUploadState.Prepared });
            command.Parameters.Add(new NpgsqlParameter("preparedBefore", NpgsqlDbType.TimestampTz) { Value = preparedBefore });
            command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });
            command.Parameters.Add(new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = token });
            command.Parameters.Add(new NpgsqlParameter("claimExpires", NpgsqlDbType.TimestampTz) { Value = now.Add(claimLease) });
            var claims = new List<PendingFileUploadCleanupClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claims.Add(new PendingFileUploadCleanupClaim(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
            }
            return claims;
        }
        finally
        {
            if (close) await _db.Database.CloseConnectionAsync();
        }
    }

    public Task<int> MarkAbandonedAsync(Guid id, Guid claimToken, DateTime nowUtc, CancellationToken ct = default) =>
        _db.PendingFileUploads
            .Where(upload => upload.Id == id
                && upload.State == PendingFileUploadState.Prepared
                && upload.CleanupClaimToken == claimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(upload => upload.State, PendingFileUploadState.Abandoned)
                .SetProperty(upload => upload.UpdatedAtUtc, AsUtc(nowUtc))
                .SetProperty(upload => upload.CleanupClaimToken, (Guid?)null)
                .SetProperty(upload => upload.CleanupClaimExpiresAtUtc, (DateTime?)null), ct);

    public Task<int> ReleaseCleanupClaimAsync(Guid id, Guid claimToken, CancellationToken ct = default) =>
        _db.PendingFileUploads
            .Where(upload => upload.Id == id
                && upload.State == PendingFileUploadState.Prepared
                && upload.CleanupClaimToken == claimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(upload => upload.CleanupClaimToken, (Guid?)null)
                .SetProperty(upload => upload.CleanupClaimExpiresAtUtc, (DateTime?)null), ct);

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
