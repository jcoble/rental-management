using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace RentalCommand.Data.Esign;

public sealed record NativeEsignExecutionClaim(int Id, string PublicId, Guid ClaimToken);

public interface INativeEsignExecutionClaimStore
{
    Task<IReadOnlyList<NativeEsignExecutionClaim>> ClaimBatchAsync(
        string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);

    Task<NativeEsignExecutionClaim?> TryClaimAsync(
        int signatureRequestId, string claimOwner, DateTime nowUtc, TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<int> ReleaseForRetryAsync(
        int signatureRequestId, Guid claimToken, string? error, CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL execution-lease boundary. The worker batch is selected, ordered, paged, locked,
/// leased, and returned by one statement. PDF generation and blob I/O begin only after that
/// statement has completed. Retry release is fenced by request id and the current claim token.
/// </summary>
public sealed class NativeEsignExecutionClaimStore : INativeEsignExecutionClaimStore
{
    // Must match AtomicLockingPersistence's RCMD + SignatureRequest advisory-lock namespace.
    private const int SignatureRequestLockNamespace = 1380142405;

    internal const string BatchClaimSql = """
        WITH candidates AS (
            SELECT request."Id"
            FROM "SignatureRequests" AS request
            INNER JOIN "Leases" AS lease ON lease."Id" = request."LeaseId"
            WHERE request."Status" = 'ExecutionPending'
              AND lease."DeletedAt" IS NULL
              AND (request."ExecutionClaimToken" IS NULL
                   OR request."ExecutionClaimExpiresAtUtc" <= @now)
              AND pg_try_advisory_xact_lock(@lockNamespace, request."Id")
              AND NOT EXISTS (
                  SELECT 1
                  FROM "SignatureSigners" AS signer
                  WHERE signer."SignatureRequestId" = request."Id"
                    AND signer."Status" <> 'Signed')
            ORDER BY request."CreatedAtUtc", request."Id"
            FOR UPDATE OF request SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "SignatureRequests" AS request
        SET "ExecutionClaimOwner" = @claimOwner,
            "ExecutionClaimToken" = @claimToken,
            "ExecutionClaimExpiresAtUtc" = @claimExpiresAtUtc,
            "ExecutionAttemptCount" = request."ExecutionAttemptCount" + 1,
            "ExecutionLastAttemptAtUtc" = @now,
            "ExecutionLastError" = NULL
        FROM candidates
        WHERE request."Id" = candidates."Id"
        RETURNING request."Id", request."PublicId", request."ExecutionClaimToken";
        """;

    internal const string SingleClaimSql = """
        WITH candidate AS (
            SELECT request."Id"
            FROM "SignatureRequests" AS request
            INNER JOIN "Leases" AS lease ON lease."Id" = request."LeaseId"
            WHERE request."Id" = @signatureRequestId
              AND request."Status" = 'ExecutionPending'
              AND lease."DeletedAt" IS NULL
              AND (request."ExecutionClaimToken" IS NULL
                   OR request."ExecutionClaimExpiresAtUtc" <= @now)
              AND pg_try_advisory_xact_lock(@lockNamespace, request."Id")
              AND NOT EXISTS (
                  SELECT 1
                  FROM "SignatureSigners" AS signer
                  WHERE signer."SignatureRequestId" = request."Id"
                    AND signer."Status" <> 'Signed')
            FOR UPDATE OF request SKIP LOCKED
        )
        UPDATE "SignatureRequests" AS request
        SET "ExecutionClaimOwner" = @claimOwner,
            "ExecutionClaimToken" = @claimToken,
            "ExecutionClaimExpiresAtUtc" = @claimExpiresAtUtc,
            "ExecutionAttemptCount" = request."ExecutionAttemptCount" + 1,
            "ExecutionLastAttemptAtUtc" = @now,
            "ExecutionLastError" = NULL
        FROM candidate
        WHERE request."Id" = candidate."Id"
        RETURNING request."Id", request."PublicId", request."ExecutionClaimToken";
        """;

    private readonly RentalCommandDbContext _db;

    public NativeEsignExecutionClaimStore(RentalCommandDbContext db) => _db = db;

    public Task<IReadOnlyList<NativeEsignExecutionClaim>> ClaimBatchAsync(
        string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return ClaimAsync(BatchClaimSql, null, claimOwner, nowUtc, leaseDuration, batchSize, ct);
    }

    public async Task<NativeEsignExecutionClaim?> TryClaimAsync(
        int signatureRequestId, string claimOwner, DateTime nowUtc, TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        if (signatureRequestId <= 0) throw new ArgumentOutOfRangeException(nameof(signatureRequestId));
        var claims = await ClaimAsync(
            SingleClaimSql, signatureRequestId, claimOwner, nowUtc, leaseDuration, 1, ct);
        return claims.Count == 0 ? null : claims[0];
    }

    public Task<int> ReleaseForRetryAsync(
        int signatureRequestId, Guid claimToken, string? error, CancellationToken ct = default) =>
        _db.SignatureRequests
            .Where(request => request.Id == signatureRequestId
                && request.Status == Core.Enums.SignatureRequestStatus.ExecutionPending
                && request.ExecutionClaimToken == claimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(request => request.ExecutionLastError, LimitError(error))
                .SetProperty(request => request.ExecutionClaimOwner, (string?)null)
                .SetProperty(request => request.ExecutionClaimToken, (Guid?)null)
                .SetProperty(request => request.ExecutionClaimExpiresAtUtc, (DateTime?)null), ct);

    private async Task<IReadOnlyList<NativeEsignExecutionClaim>> ClaimAsync(
        string sql,
        int? signatureRequestId,
        string claimOwner,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var now = AsUtc(nowUtc);
        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            // Use EF's open path so API/Engine connection interceptors establish RLS session state.
            await _db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
            command.Parameters.Add(new NpgsqlParameter("lockNamespace", NpgsqlDbType.Integer)
            {
                Value = SignatureRequestLockNamespace,
            });
            command.Parameters.Add(new NpgsqlParameter("claimExpiresAtUtc", NpgsqlDbType.TimestampTz)
            {
                Value = now.Add(leaseDuration),
            });
            if (signatureRequestId.HasValue)
            {
                command.Parameters.Add(new NpgsqlParameter("signatureRequestId", NpgsqlDbType.Integer)
                {
                    Value = signatureRequestId.Value,
                });
            }
            else
            {
                command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });
            }

            var claims = new List<NativeEsignExecutionClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claims.Add(new NativeEsignExecutionClaim(
                    reader.GetInt32(0), reader.GetString(1), reader.GetGuid(2)));
            }

            return claims;
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    private static string? LimitError(string? error) => string.IsNullOrWhiteSpace(error)
        ? null
        : error.Length <= 2000 ? error : error[..2000];

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
