using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace RentalCommand.Data.Esign;

public sealed record NativeEsignExecutionClaim(int Id, Guid PublicId, Guid ClaimToken);

public interface INativeEsignExecutionClaimStore
{
    Task<IReadOnlyList<NativeEsignExecutionClaim>> ClaimBatchAsync(
        string claimOwner, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);

    Task<NativeEsignExecutionClaim?> TryClaimAsync(
        int signatureRequestId, string claimOwner, TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<int> ReleaseForRetryAsync(
        int signatureRequestId, Guid claimToken, string? error, CancellationToken ct = default);

    Task<bool> HasCompletedAgreementFinancialReconciliationsAsync(
        CancellationToken ct = default);
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
        WITH clock AS MATERIALIZED (
            SELECT clock_timestamp() AS now_utc
        ), candidates AS MATERIALIZED (
            SELECT request."Id"
            FROM "SignatureRequests" AS request
            LEFT JOIN "LeaseAgreements" AS agreement ON agreement."Id" = request."LeaseAgreementId"
            LEFT JOIN "LeaseAddenda" AS addendum ON addendum."Id" = request."LeaseAddendumId"
            CROSS JOIN clock
            WHERE request."Status" = 'ExecutionPending'
              AND ((request."LeaseAgreementId" IS NOT NULL AND agreement."Id" IS NOT NULL
                    AND agreement."VoidedAtUtc" IS NULL)
                   OR (request."LeaseAddendumId" IS NOT NULL AND addendum."Id" IS NOT NULL
                       AND addendum."VoidedAtUtc" IS NULL))
              AND (request."ExecutionClaimToken" IS NULL
                   OR request."ExecutionClaimExpiresAtUtc" <= clock.now_utc)
              AND NOT EXISTS (
                  SELECT 1
                  FROM "SignatureSigners" AS signer
                  WHERE signer."SignatureRequestId" = request."Id"
                    AND signer."Status" <> 'Signed')
            ORDER BY request."PreparedAtUtc", request."Id"
            FOR UPDATE OF request SKIP LOCKED
            LIMIT @batchSize
        ), locked_candidates AS (
            SELECT candidate."Id"
            FROM candidates AS candidate
            WHERE pg_try_advisory_xact_lock(@lockNamespace, candidate."Id")
        )
        UPDATE "SignatureRequests" AS request
        SET "ExecutionClaimOwner" = @claimOwner,
            "ExecutionClaimToken" = gen_random_uuid(),
            "ExecutionClaimExpiresAtUtc" = clock.now_utc + @leaseDuration,
            "ExecutionAttemptCount" = request."ExecutionAttemptCount" + 1,
            "ExecutionLastAttemptAtUtc" = clock.now_utc,
            "LastError" = NULL
        FROM locked_candidates, clock
        WHERE request."Id" = locked_candidates."Id"
        RETURNING request."Id", request."PublicId", request."ExecutionClaimToken";
        """;

    internal const string SingleClaimSql = """
        WITH clock AS MATERIALIZED (
            SELECT clock_timestamp() AS now_utc
        ), candidate AS (
            SELECT request."Id"
            FROM "SignatureRequests" AS request
            LEFT JOIN "LeaseAgreements" AS agreement ON agreement."Id" = request."LeaseAgreementId"
            LEFT JOIN "LeaseAddenda" AS addendum ON addendum."Id" = request."LeaseAddendumId"
            CROSS JOIN clock
            WHERE request."Id" = @signatureRequestId
              AND request."Status" = 'ExecutionPending'
              AND ((request."LeaseAgreementId" IS NOT NULL AND agreement."Id" IS NOT NULL
                    AND agreement."VoidedAtUtc" IS NULL)
                   OR (request."LeaseAddendumId" IS NOT NULL AND addendum."Id" IS NOT NULL
                       AND addendum."VoidedAtUtc" IS NULL))
              AND (request."ExecutionClaimToken" IS NULL
                   OR request."ExecutionClaimExpiresAtUtc" <= clock.now_utc)
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
            "ExecutionClaimToken" = gen_random_uuid(),
            "ExecutionClaimExpiresAtUtc" = clock.now_utc + @leaseDuration,
            "ExecutionAttemptCount" = request."ExecutionAttemptCount" + 1,
            "ExecutionLastAttemptAtUtc" = clock.now_utc,
            "LastError" = NULL
        FROM candidate, clock
        WHERE request."Id" = candidate."Id"
        RETURNING request."Id", request."PublicId", request."ExecutionClaimToken";
        """;

    private const string ReleaseForRetrySql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "SignatureRequests" AS request
        SET "LastError" = @error,
            "ExecutionClaimOwner" = NULL,
            "ExecutionClaimToken" = NULL,
            "ExecutionClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE request."Id" = @signatureRequestId
          AND request."Status" = 'ExecutionPending'
          AND request."ExecutionClaimToken" = @claimToken
          AND request."ExecutionClaimExpiresAtUtc" > clock.now_utc;
        """;

    internal const string CompletedAgreementFinancialReconciliationExistsSql = """
        SELECT EXISTS (
            SELECT 1
            FROM "SignatureRequests" AS request
            JOIN "LeaseAgreements" AS agreement
              ON agreement."Id" = request."LeaseAgreementId"
             AND agreement."PortfolioId" = request."PortfolioId"
            JOIN "LeaseManagements" AS management
              ON management."Id" = agreement."LeaseManagementId"
             AND management."PortfolioId" = agreement."PortfolioId"
            JOIN "TenantAccounts" AS account
              ON account."LeaseManagementId" = management."Id"
             AND account."PortfolioId" = management."PortfolioId"
            WHERE request."Status" = 'Completed'
              AND request."ExecutedArtifactId" IS NOT NULL
              AND request."LeaseAgreementId" IS NOT NULL
              AND request."LeaseAddendumId" IS NULL
              AND agreement."ChangeType" = 'Initial'
              AND agreement."ReplacesAgreementId" IS NULL
              AND agreement."RenewsAgreementId" IS NULL
              AND agreement."FullyExecutedAtUtc" IS NOT NULL
              AND agreement."ExecutedArtifactId" IS NOT NULL
              AND agreement."VoidedAtUtc" IS NULL
              AND agreement."DraftCanceledAtUtc" IS NULL
              AND agreement."SecurityDepositObligation" > 0
              AND management."CanceledAtUtc" IS NULL
              AND account."ClosedAtUtc" IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "TenantLedgerEntries" AS ledger
                  WHERE ledger."TenantAccountId" = account."Id"
                    AND ledger."BusinessKey" =
                        'security-deposit:agreement:' || agreement."PublicId"::text)
            LIMIT 1);
        """;

    private readonly RentalCommandDbContext _db;

    public NativeEsignExecutionClaimStore(RentalCommandDbContext db) => _db = db;

    public Task<IReadOnlyList<NativeEsignExecutionClaim>> ClaimBatchAsync(
        string claimOwner, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return ClaimAsync(BatchClaimSql, null, claimOwner, leaseDuration, batchSize, ct);
    }

    public async Task<NativeEsignExecutionClaim?> TryClaimAsync(
        int signatureRequestId, string claimOwner, TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        if (signatureRequestId <= 0) throw new ArgumentOutOfRangeException(nameof(signatureRequestId));
        var claims = await ClaimAsync(
            SingleClaimSql, signatureRequestId, claimOwner, leaseDuration, 1, ct);
        return claims.Count == 0 ? null : claims[0];
    }

    public async Task<int> ReleaseForRetryAsync(
        int signatureRequestId, Guid claimToken, string? error, CancellationToken ct = default)
    {
        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ReleaseForRetrySql;
            command.Parameters.Add(new NpgsqlParameter("signatureRequestId", NpgsqlDbType.Integer)
                { Value = signatureRequestId });
            command.Parameters.Add(new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = claimToken });
            command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text)
                { Value = (object?)LimitError(error) ?? DBNull.Value });
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    public async Task<bool> HasCompletedAgreementFinancialReconciliationsAsync(
        CancellationToken ct = default)
    {
        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = CompletedAgreementFinancialReconciliationExistsSql;
            var value = await command.ExecuteScalarAsync(ct);
            return value is bool candidate && candidate;
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    private async Task<IReadOnlyList<NativeEsignExecutionClaim>> ClaimAsync(
        string sql,
        int? signatureRequestId,
        string claimOwner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));

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
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("lockNamespace", NpgsqlDbType.Integer)
            {
                Value = SignatureRequestLockNamespace,
            });
            command.Parameters.Add(new NpgsqlParameter("leaseDuration", NpgsqlDbType.Interval)
                { Value = leaseDuration });
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
                    reader.GetInt32(0), reader.GetGuid(1), reader.GetGuid(2)));
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

}
