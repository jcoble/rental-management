using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Accounting;

public enum AccountingWorkerOperation { Pull, TokenRotation }

public sealed record AccountingWorkerFence(
    AccountingWorkerOperation Operation, Guid ClaimToken, Guid? ParentPullClaimToken = null);
public sealed record AccountingConnectionClaim(AccountingConnection Connection, AccountingWorkerFence Fence);

public interface IAccountingConnectionClaimStore
{
    Task<IReadOnlyList<AccountingConnectionClaim>> ClaimPullAsync(
        string claimOwner, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<AccountingConnectionClaim>> ClaimTokenRefreshAsync(
        string claimOwner, TimeSpan refreshHorizon, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
    Task<AccountingConnectionClaim?> ClaimInlineTokenRotationAsync(
        int connectionId, Guid? pullClaimToken, string claimOwner,
        TimeSpan leaseDuration, CancellationToken ct = default);
    Task<int> MarkPullFailedAsync(
        int id, Guid claimToken, TimeSpan retryDelay, string error,
        CancellationToken ct = default);
    Task<int> MarkTokenRotationRecoveryRequiredAsync(
        int id, Guid claimToken, string error, CancellationToken ct = default);
    Task<int> CompleteTokenRotationAsync(
        int id, Guid claimToken, Guid? parentPullClaimToken,
        string accessTokenCipherText, string refreshTokenCipherText,
        DateTime expiresAtUtc, CancellationToken ct = default);
    Task<int> ReconcileAbandonedTokenRotationsAsync(CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL boundaries for fair, bounded accounting pull and token-refresh claims. Each claim is
/// a single UPDATE over a DB-filtered/ordered <c>FOR UPDATE SKIP LOCKED</c> candidate CTE. Remote work
/// starts only after the statement closes. All refresh-token callers share one durable rotation lane.
/// </summary>
public sealed class AccountingConnectionClaimStore : IAccountingConnectionClaimStore
{
    private const string ReturnedColumns = """
        connection."Id", connection."PortfolioId", connection."Provider", connection."Status",
        connection."ExternalAccountId", connection."CompanyName", connection."AccessTokenCipherText",
        connection."RefreshTokenCipherText", connection."TokenExpiresAt", connection."PullEnabled",
        connection."PushEnabled", connection."LastPulledAtJson"::text, connection."LastError",
        connection."LastSyncedAt", connection."ConnectedAt", connection."DisconnectedAt",
        connection."NextPullAtUtc",
        connection."PullClaimOwner", connection."PullClaimToken", connection."PullClaimExpiresAtUtc",
        connection."PullAttemptCount", connection."PullLastAttemptAtUtc",
        connection."TokenRotationState", connection."TokenGeneration",
        connection."TokenRotationClaimOwner", connection."TokenRotationClaimToken", connection."TokenRotationClaimExpiresAtUtc",
        connection."TokenRotationAttemptCount", connection."TokenRotationLastAttemptAtUtc",
        connection."CreatedAt", connection."UpdatedAt"
        """;

    private static readonly string PullClaimSql = $$"""
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc), candidates AS (
            SELECT connection."Id"
            FROM "AccountingConnections" AS connection
            INNER JOIN "Portfolios" AS portfolio ON portfolio."Id" = connection."PortfolioId"
            CROSS JOIN clock
            WHERE portfolio."DeletedAt" IS NULL
              AND connection."Status" = @connected
              AND connection."PullEnabled"
              AND connection."NextPullAtUtc" <= clock.now_utc
              AND (connection."PullClaimToken" IS NULL OR connection."PullClaimExpiresAtUtc" <= clock.now_utc)
            ORDER BY connection."NextPullAtUtc", connection."Id"
            FOR UPDATE OF connection SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "AccountingConnections" AS connection
        SET "PullClaimOwner" = @claimOwner,
            "PullClaimToken" = gen_random_uuid(),
            "PullClaimExpiresAtUtc" = clock.now_utc + @leaseDuration,
            "PullAttemptCount" = connection."PullAttemptCount" + 1,
            "PullLastAttemptAtUtc" = clock.now_utc
        FROM candidates, clock
        WHERE connection."Id" = candidates."Id"
        RETURNING {{ReturnedColumns}};
        """;

    private static readonly string RefreshClaimSql = $$"""
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc), candidates AS (
            SELECT connection."Id"
            FROM "AccountingConnections" AS connection
            INNER JOIN "Portfolios" AS portfolio ON portfolio."Id" = connection."PortfolioId"
            CROSS JOIN clock
            WHERE portfolio."DeletedAt" IS NULL
              AND connection."Status" = @connected
              AND connection."RefreshTokenCipherText" IS NOT NULL
              AND connection."TokenExpiresAt" IS NOT NULL
              AND connection."TokenExpiresAt" < clock.now_utc + @refreshHorizon
              AND connection."TokenRotationState" = @rotationIdle
              AND connection."TokenRotationClaimToken" IS NULL
            ORDER BY connection."TokenExpiresAt", connection."Id"
            FOR UPDATE OF connection SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "AccountingConnections" AS connection
        SET "TokenRotationState" = @rotationInFlight,
            "TokenRotationClaimOwner" = @claimOwner,
            "TokenRotationClaimToken" = gen_random_uuid(),
            "TokenRotationClaimExpiresAtUtc" = clock.now_utc + @leaseDuration,
            "TokenRotationAttemptCount" = connection."TokenRotationAttemptCount" + 1,
            "TokenRotationLastAttemptAtUtc" = clock.now_utc
        FROM candidates, clock
        WHERE connection."Id" = candidates."Id"
        RETURNING {{ReturnedColumns}};
        """;

    private static readonly string InlineRotationClaimSql = $$"""
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc), candidate AS (
            SELECT connection."Id"
            FROM "AccountingConnections" AS connection
            INNER JOIN "Portfolios" AS portfolio ON portfolio."Id" = connection."PortfolioId"
            CROSS JOIN clock
            WHERE connection."Id" = @connectionId
              AND portfolio."DeletedAt" IS NULL
              AND connection."Status" = @connected
              AND connection."PullEnabled"
              AND connection."RefreshTokenCipherText" IS NOT NULL
              AND connection."TokenRotationState" = @rotationIdle
              AND connection."TokenRotationClaimToken" IS NULL
              AND (@pullClaimToken IS NULL OR connection."PullClaimToken" = @pullClaimToken)
            FOR UPDATE OF connection SKIP LOCKED
        )
        UPDATE "AccountingConnections" AS connection
        SET "TokenRotationState" = @rotationInFlight,
            "TokenRotationClaimOwner" = @claimOwner,
            "TokenRotationClaimToken" = gen_random_uuid(),
            "TokenRotationClaimExpiresAtUtc" = clock.now_utc + @leaseDuration,
            "TokenRotationAttemptCount" = connection."TokenRotationAttemptCount" + 1,
            "TokenRotationLastAttemptAtUtc" = clock.now_utc
        FROM candidate, clock
        WHERE connection."Id" = candidate."Id"
        RETURNING {{ReturnedColumns}};
        """;

    private const string MarkPullFailedSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "AccountingConnections" AS connection
        SET "LastError" = @error,
            "UpdatedAt" = clock.now_utc,
            "NextPullAtUtc" = clock.now_utc + @retryDelay,
            "PullClaimOwner" = NULL,
            "PullClaimToken" = NULL,
            "PullClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE connection."Id" = @id
          AND connection."PullClaimToken" = @claimToken
          AND connection."PullClaimExpiresAtUtc" > clock.now_utc;
        """;

    private const string MarkRotationRecoverySql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "AccountingConnections" AS connection
        SET "Status" = @needsReconnect,
            "AccessTokenCipherText" = NULL,
            "RefreshTokenCipherText" = NULL,
            "TokenExpiresAt" = NULL,
            "TokenRotationState" = @recoveryRequired,
            "LastError" = @error,
            "UpdatedAt" = clock.now_utc
        FROM clock
        WHERE connection."Id" = @id
          AND connection."TokenRotationState" = @rotationInFlight
          AND connection."TokenRotationClaimToken" = @claimToken
          AND connection."TokenRotationClaimExpiresAtUtc" > clock.now_utc;
        """;

    private const string CompleteRotationSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "AccountingConnections" AS connection
        SET "AccessTokenCipherText" = @accessToken,
            "RefreshTokenCipherText" = @refreshToken,
            "TokenExpiresAt" = @expiresAtUtc,
            "TokenGeneration" = connection."TokenGeneration" + 1,
            "TokenRotationState" = @rotationIdle,
            "TokenRotationClaimOwner" = NULL,
            "TokenRotationClaimToken" = NULL,
            "TokenRotationClaimExpiresAtUtc" = NULL,
            "LastError" = NULL,
            "UpdatedAt" = clock.now_utc
        FROM clock
        WHERE connection."Id" = @id
          AND connection."Status" = @connected
          AND connection."TokenRotationState" = @rotationInFlight
          AND connection."TokenRotationClaimToken" = @claimToken
          AND connection."TokenRotationClaimExpiresAtUtc" > clock.now_utc
          AND (@parentPullClaimToken IS NULL
               OR (connection."PullEnabled" AND connection."PullClaimToken" = @parentPullClaimToken));
        """;

    private const string ReconcileRotationsSql = """
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "AccountingConnections" AS connection
        SET "Status" = @needsReconnect,
            "AccessTokenCipherText" = NULL,
            "RefreshTokenCipherText" = NULL,
            "TokenExpiresAt" = NULL,
            "TokenRotationState" = @recoveryRequired,
            "LastError" = 'Token rotation outcome could not be confirmed. Reconnect the accounting provider.',
            "UpdatedAt" = clock.now_utc
        FROM clock
        WHERE connection."Status" = @connected
          AND connection."TokenRotationState" = @rotationInFlight
          AND connection."TokenRotationClaimExpiresAtUtc" <= clock.now_utc;
        """;

    private readonly RentalCommandDbContext _db;
    public AccountingConnectionClaimStore(RentalCommandDbContext db) => _db = db;

    public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimPullAsync(
        string claimOwner, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimAsync(PullClaimSql, AccountingWorkerOperation.Pull, claimOwner, null, leaseDuration, batchSize, ct);

    public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimTokenRefreshAsync(
        string claimOwner, TimeSpan refreshHorizon, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimRefreshAfterReconciliationAsync(claimOwner, refreshHorizon, leaseDuration, batchSize, ct);

    public async Task<AccountingConnectionClaim?> ClaimInlineTokenRotationAsync(
        int connectionId, Guid? pullClaimToken, string claimOwner,
        TimeSpan leaseDuration, CancellationToken ct = default)
    {
        await ReconcileAbandonedTokenRotationsAsync(ct);
        var claimed = (await ClaimAsync(
            InlineRotationClaimSql, AccountingWorkerOperation.TokenRotation, claimOwner,
            null, leaseDuration, 1, ct, connectionId, pullClaimToken)).SingleOrDefault();
        return claimed is null
            ? null
            : claimed with { Fence = claimed.Fence with { ParentPullClaimToken = pullClaimToken } };
    }

    public Task<int> MarkPullFailedAsync(
        int id, Guid claimToken, TimeSpan retryDelay, string error,
        CancellationToken ct = default)
    {
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        return ExecuteMutationAsync(MarkPullFailedSql, id, claimToken, ct,
            new NpgsqlParameter("retryDelay", NpgsqlDbType.Interval) { Value = retryDelay },
            new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = LimitError(error) });
    }

    public Task<int> MarkTokenRotationRecoveryRequiredAsync(
        int id, Guid claimToken, string error, CancellationToken ct = default) =>
        ExecuteMutationAsync(MarkRotationRecoverySql, id, claimToken, ct,
            RotationParameter("rotationInFlight", AccountingTokenRotationState.InFlight),
            StatusParameter("needsReconnect", AccountingConnectionStatus.NeedsReconnect),
            RotationParameter("recoveryRequired", AccountingTokenRotationState.RecoveryRequired),
            new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = LimitError(error) });

    public Task<int> CompleteTokenRotationAsync(
        int id, Guid claimToken, Guid? parentPullClaimToken,
        string accessTokenCipherText, string refreshTokenCipherText,
        DateTime expiresAtUtc, CancellationToken ct = default) =>
        ExecuteMutationAsync(CompleteRotationSql, id, claimToken, ct,
            StatusParameter("connected", AccountingConnectionStatus.Connected),
            RotationParameter("rotationInFlight", AccountingTokenRotationState.InFlight),
            RotationParameter("rotationIdle", AccountingTokenRotationState.Idle),
            new NpgsqlParameter("parentPullClaimToken", NpgsqlDbType.Uuid)
                { Value = parentPullClaimToken.HasValue ? parentPullClaimToken.Value : DBNull.Value },
            new NpgsqlParameter("accessToken", NpgsqlDbType.Text) { Value = accessTokenCipherText },
            new NpgsqlParameter("refreshToken", NpgsqlDbType.Text) { Value = refreshTokenCipherText },
            new NpgsqlParameter("expiresAtUtc", NpgsqlDbType.TimestampTz) { Value = AsUtc(expiresAtUtc) });

    public Task<int> ReconcileAbandonedTokenRotationsAsync(CancellationToken ct = default) =>
        ExecuteMutationAsync(ReconcileRotationsSql, null, null, ct,
            StatusParameter("connected", AccountingConnectionStatus.Connected),
            StatusParameter("needsReconnect", AccountingConnectionStatus.NeedsReconnect),
            RotationParameter("rotationInFlight", AccountingTokenRotationState.InFlight),
            RotationParameter("recoveryRequired", AccountingTokenRotationState.RecoveryRequired));

    private async Task<IReadOnlyList<AccountingConnectionClaim>> ClaimRefreshAfterReconciliationAsync(
        string claimOwner, TimeSpan refreshHorizon, TimeSpan leaseDuration,
        int batchSize, CancellationToken ct)
    {
        await ReconcileAbandonedTokenRotationsAsync(ct);
        return await ClaimAsync(RefreshClaimSql, AccountingWorkerOperation.TokenRotation, claimOwner,
            refreshHorizon, leaseDuration, batchSize, ct);
    }

    private async Task<IReadOnlyList<AccountingConnectionClaim>> ClaimAsync(
        string sql, AccountingWorkerOperation operation, string claimOwner,
        TimeSpan? refreshHorizon, TimeSpan leaseDuration, int batchSize, CancellationToken ct,
        int? connectionId = null, Guid? pullClaimToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (refreshHorizon is TimeSpan validatedHorizon && validatedHorizon < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(refreshHorizon));
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("connected", NpgsqlDbType.Integer)
                { Value = (int)AccountingConnectionStatus.Connected });
            command.Parameters.Add(new NpgsqlParameter("rotationIdle", NpgsqlDbType.Integer)
                { Value = (int)AccountingTokenRotationState.Idle });
            command.Parameters.Add(new NpgsqlParameter("rotationInFlight", NpgsqlDbType.Integer)
                { Value = (int)AccountingTokenRotationState.InFlight });
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("leaseDuration", NpgsqlDbType.Interval)
                { Value = leaseDuration });
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });
            if (refreshHorizon is TimeSpan horizon)
                command.Parameters.Add(new NpgsqlParameter("refreshHorizon", NpgsqlDbType.Interval)
                    { Value = horizon });
            if (connectionId.HasValue)
            {
                command.Parameters.Add(new NpgsqlParameter("connectionId", NpgsqlDbType.Integer)
                    { Value = connectionId.Value });
                command.Parameters.Add(new NpgsqlParameter("pullClaimToken", NpgsqlDbType.Uuid)
                    { Value = pullClaimToken.HasValue ? pullClaimToken.Value : DBNull.Value });
            }

            var claims = new List<AccountingConnectionClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var claimed = ReadConnection(reader);
                var token = operation == AccountingWorkerOperation.Pull
                    ? claimed.PullClaimToken!.Value
                    : claimed.TokenRotationClaimToken!.Value;
                claims.Add(new AccountingConnectionClaim(claimed, new AccountingWorkerFence(operation, token)));
            }
            return claims;
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    private async Task<int> ExecuteMutationAsync(
        string sql, int? id, Guid? claimToken, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (id.HasValue) command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = id.Value });
            if (claimToken.HasValue)
                command.Parameters.Add(new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = claimToken.Value });
            foreach (var parameter in parameters) command.Parameters.Add(parameter);
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    private static NpgsqlParameter StatusParameter(string name, AccountingConnectionStatus value) =>
        new(name, NpgsqlDbType.Integer) { Value = (int)value };
    private static NpgsqlParameter RotationParameter(string name, AccountingTokenRotationState value) =>
        new(name, NpgsqlDbType.Integer) { Value = (int)value };

    private static AccountingConnection ReadConnection(System.Data.Common.DbDataReader reader) => new()
    {
        Id = reader.GetInt32(0), PortfolioId = reader.GetInt32(1), Provider = (AccountingProvider)reader.GetInt32(2),
        Status = (AccountingConnectionStatus)reader.GetInt32(3), ExternalAccountId = String(reader, 4),
        CompanyName = String(reader, 5), AccessTokenCipherText = String(reader, 6),
        RefreshTokenCipherText = String(reader, 7), TokenExpiresAt = Date(reader, 8), PullEnabled = reader.GetBoolean(9),
        PushEnabled = reader.GetBoolean(10), LastPulledAtJson = String(reader, 11), LastError = String(reader, 12),
        LastSyncedAt = Date(reader, 13), ConnectedAt = Date(reader, 14), DisconnectedAt = Date(reader, 15),
        NextPullAtUtc = reader.GetDateTime(16), PullClaimOwner = String(reader, 17),
        PullClaimToken = GuidValue(reader, 18), PullClaimExpiresAtUtc = Date(reader, 19),
        PullAttemptCount = reader.GetInt32(20), PullLastAttemptAtUtc = Date(reader, 21),
        TokenRotationState = (AccountingTokenRotationState)reader.GetInt32(22), TokenGeneration = reader.GetInt64(23),
        TokenRotationClaimOwner = String(reader, 24), TokenRotationClaimToken = GuidValue(reader, 25),
        TokenRotationClaimExpiresAtUtc = Date(reader, 26), TokenRotationAttemptCount = reader.GetInt32(27),
        TokenRotationLastAttemptAtUtc = Date(reader, 28), CreatedAt = reader.GetDateTime(29), UpdatedAt = reader.GetDateTime(30),
    };

    private static string? String(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTime? Date(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
    private static Guid? GuidValue(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
    private static string LimitError(string error) => string.IsNullOrWhiteSpace(error)
        ? "Accounting operation failed without an error message."
        : error.Length <= 2000 ? error : error[..2000];
}
