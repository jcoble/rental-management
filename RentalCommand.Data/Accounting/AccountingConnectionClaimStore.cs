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
        string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<AccountingConnectionClaim>> ClaimTokenRefreshAsync(
        string claimOwner, DateTime nowUtc, DateTime refreshBeforeUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
    Task<AccountingConnectionClaim?> ClaimInlineTokenRotationAsync(
        int connectionId, Guid? pullClaimToken, string claimOwner, DateTime nowUtc,
        TimeSpan leaseDuration, CancellationToken ct = default);
    Task<int> MarkPullFailedAsync(
        int id, Guid claimToken, DateTime failedAtUtc, DateTime nextPullAtUtc, string error,
        CancellationToken ct = default);
    Task<int> MarkTokenRotationRecoveryRequiredAsync(
        int id, Guid claimToken, DateTime failedAtUtc, string error, CancellationToken ct = default);
    Task<int> CompleteTokenRotationAsync(
        int id, Guid claimToken, Guid? parentPullClaimToken,
        string accessTokenCipherText, string refreshTokenCipherText,
        DateTime expiresAtUtc, DateTime completedAtUtc, CancellationToken ct = default);
    Task<int> ReconcileAbandonedTokenRotationsAsync(DateTime nowUtc, CancellationToken ct = default);
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
        WITH candidates AS (
            SELECT connection."Id"
            FROM "AccountingConnections" AS connection
            INNER JOIN "Portfolios" AS portfolio ON portfolio."Id" = connection."PortfolioId"
            WHERE portfolio."DeletedAt" IS NULL
              AND connection."Status" = @connected
              AND connection."PullEnabled"
              AND connection."NextPullAtUtc" <= @now
              AND (connection."PullClaimToken" IS NULL OR connection."PullClaimExpiresAtUtc" <= @now)
            ORDER BY connection."NextPullAtUtc", connection."Id"
            FOR UPDATE OF connection SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "AccountingConnections" AS connection
        SET "PullClaimOwner" = @claimOwner,
            "PullClaimToken" = @claimToken,
            "PullClaimExpiresAtUtc" = @claimExpiresAtUtc,
            "PullAttemptCount" = connection."PullAttemptCount" + 1,
            "PullLastAttemptAtUtc" = @now
        FROM candidates
        WHERE connection."Id" = candidates."Id"
        RETURNING {{ReturnedColumns}};
        """;

    private static readonly string RefreshClaimSql = $$"""
        WITH candidates AS (
            SELECT connection."Id"
            FROM "AccountingConnections" AS connection
            INNER JOIN "Portfolios" AS portfolio ON portfolio."Id" = connection."PortfolioId"
            WHERE portfolio."DeletedAt" IS NULL
              AND connection."Status" = @connected
              AND connection."RefreshTokenCipherText" IS NOT NULL
              AND connection."TokenExpiresAt" IS NOT NULL
              AND connection."TokenExpiresAt" < @refreshBefore
              AND connection."TokenRotationState" = @rotationIdle
              AND connection."TokenRotationClaimToken" IS NULL
            ORDER BY connection."TokenExpiresAt", connection."Id"
            FOR UPDATE OF connection SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "AccountingConnections" AS connection
        SET "TokenRotationState" = @rotationInFlight,
            "TokenRotationClaimOwner" = @claimOwner,
            "TokenRotationClaimToken" = @claimToken,
            "TokenRotationClaimExpiresAtUtc" = @claimExpiresAtUtc,
            "TokenRotationAttemptCount" = connection."TokenRotationAttemptCount" + 1,
            "TokenRotationLastAttemptAtUtc" = @now
        FROM candidates
        WHERE connection."Id" = candidates."Id"
        RETURNING {{ReturnedColumns}};
        """;

    private static readonly string InlineRotationClaimSql = $$"""
        WITH candidate AS (
            SELECT connection."Id"
            FROM "AccountingConnections" AS connection
            INNER JOIN "Portfolios" AS portfolio ON portfolio."Id" = connection."PortfolioId"
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
            "TokenRotationClaimToken" = @claimToken,
            "TokenRotationClaimExpiresAtUtc" = @claimExpiresAtUtc,
            "TokenRotationAttemptCount" = connection."TokenRotationAttemptCount" + 1,
            "TokenRotationLastAttemptAtUtc" = @now
        FROM candidate
        WHERE connection."Id" = candidate."Id"
        RETURNING {{ReturnedColumns}};
        """;

    private readonly RentalCommandDbContext _db;
    public AccountingConnectionClaimStore(RentalCommandDbContext db) => _db = db;

    public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimPullAsync(
        string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimAsync(PullClaimSql, AccountingWorkerOperation.Pull, claimOwner, nowUtc, null, leaseDuration, batchSize, ct);

    public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimTokenRefreshAsync(
        string claimOwner, DateTime nowUtc, DateTime refreshBeforeUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimRefreshAfterReconciliationAsync(claimOwner, nowUtc, refreshBeforeUtc, leaseDuration, batchSize, ct);

    public async Task<AccountingConnectionClaim?> ClaimInlineTokenRotationAsync(
        int connectionId, Guid? pullClaimToken, string claimOwner, DateTime nowUtc,
        TimeSpan leaseDuration, CancellationToken ct = default)
    {
        await ReconcileAbandonedTokenRotationsAsync(nowUtc, ct);
        var claimed = (await ClaimAsync(
            InlineRotationClaimSql, AccountingWorkerOperation.TokenRotation, claimOwner, nowUtc,
            null, leaseDuration, 1, ct, connectionId, pullClaimToken)).SingleOrDefault();
        return claimed is null
            ? null
            : claimed with { Fence = claimed.Fence with { ParentPullClaimToken = pullClaimToken } };
    }

    public Task<int> MarkPullFailedAsync(
        int id, Guid claimToken, DateTime failedAtUtc, DateTime nextPullAtUtc, string error,
        CancellationToken ct = default) =>
        PullOwned(id, claimToken).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.LastError, LimitError(error))
            .SetProperty(row => row.UpdatedAt, AsUtc(failedAtUtc))
            .SetProperty(row => row.NextPullAtUtc, AsUtc(nextPullAtUtc))
            .SetProperty(row => row.PullClaimOwner, (string?)null)
            .SetProperty(row => row.PullClaimToken, (Guid?)null)
            .SetProperty(row => row.PullClaimExpiresAtUtc, (DateTime?)null), ct);

    public Task<int> MarkTokenRotationRecoveryRequiredAsync(
        int id, Guid claimToken, DateTime failedAtUtc, string error, CancellationToken ct = default) =>
        RotationOwned(id, claimToken).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Status, AccountingConnectionStatus.NeedsReconnect)
            .SetProperty(row => row.AccessTokenCipherText, (string?)null)
            .SetProperty(row => row.RefreshTokenCipherText, (string?)null)
            .SetProperty(row => row.TokenExpiresAt, (DateTime?)null)
            .SetProperty(row => row.TokenRotationState, AccountingTokenRotationState.RecoveryRequired)
            .SetProperty(row => row.LastError, LimitError(error))
            .SetProperty(row => row.UpdatedAt, AsUtc(failedAtUtc)), ct);

    public Task<int> CompleteTokenRotationAsync(
        int id, Guid claimToken, Guid? parentPullClaimToken,
        string accessTokenCipherText, string refreshTokenCipherText,
        DateTime expiresAtUtc, DateTime completedAtUtc, CancellationToken ct = default) =>
        RotationOwned(id, claimToken)
            .Where(row => row.Status == AccountingConnectionStatus.Connected)
            .Where(row => !parentPullClaimToken.HasValue
                || (row.PullEnabled && row.PullClaimToken == parentPullClaimToken))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.AccessTokenCipherText, accessTokenCipherText)
                .SetProperty(row => row.RefreshTokenCipherText, refreshTokenCipherText)
                .SetProperty(row => row.TokenExpiresAt, AsUtc(expiresAtUtc))
                .SetProperty(row => row.TokenGeneration, row => row.TokenGeneration + 1)
                .SetProperty(row => row.TokenRotationState, AccountingTokenRotationState.Idle)
                .SetProperty(row => row.TokenRotationClaimOwner, (string?)null)
                .SetProperty(row => row.TokenRotationClaimToken, (Guid?)null)
                .SetProperty(row => row.TokenRotationClaimExpiresAtUtc, (DateTime?)null)
                .SetProperty(row => row.LastError, (string?)null)
                .SetProperty(row => row.UpdatedAt, AsUtc(completedAtUtc)), ct);

    public Task<int> ReconcileAbandonedTokenRotationsAsync(DateTime nowUtc, CancellationToken ct = default) =>
        _db.AccountingConnections.IgnoreQueryFilters()
            .Where(row => row.Status == AccountingConnectionStatus.Connected
                && row.TokenRotationState == AccountingTokenRotationState.InFlight
                && row.TokenRotationClaimExpiresAtUtc <= AsUtc(nowUtc))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, AccountingConnectionStatus.NeedsReconnect)
                .SetProperty(row => row.AccessTokenCipherText, (string?)null)
                .SetProperty(row => row.RefreshTokenCipherText, (string?)null)
                .SetProperty(row => row.TokenExpiresAt, (DateTime?)null)
                .SetProperty(row => row.TokenRotationState, AccountingTokenRotationState.RecoveryRequired)
                .SetProperty(row => row.LastError,
                    "Token rotation outcome could not be confirmed. Reconnect the accounting provider.")
                .SetProperty(row => row.UpdatedAt, AsUtc(nowUtc)), ct);

    private async Task<IReadOnlyList<AccountingConnectionClaim>> ClaimRefreshAfterReconciliationAsync(
        string claimOwner, DateTime nowUtc, DateTime refreshBeforeUtc, TimeSpan leaseDuration,
        int batchSize, CancellationToken ct)
    {
        await ReconcileAbandonedTokenRotationsAsync(nowUtc, ct);
        return await ClaimAsync(RefreshClaimSql, AccountingWorkerOperation.TokenRotation, claimOwner,
            nowUtc, refreshBeforeUtc, leaseDuration, batchSize, ct);
    }

    private async Task<IReadOnlyList<AccountingConnectionClaim>> ClaimAsync(
        string sql, AccountingWorkerOperation operation, string claimOwner, DateTime nowUtc,
        DateTime? refreshBeforeUtc, TimeSpan leaseDuration, int batchSize, CancellationToken ct,
        int? connectionId = null, Guid? pullClaimToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var now = AsUtc(nowUtc);
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
            command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("claimToken", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
            command.Parameters.Add(new NpgsqlParameter("claimExpiresAtUtc", NpgsqlDbType.TimestampTz)
                { Value = now.Add(leaseDuration) });
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });
            if (refreshBeforeUtc is DateTime refreshBefore)
                command.Parameters.Add(new NpgsqlParameter("refreshBefore", NpgsqlDbType.TimestampTz)
                    { Value = AsUtc(refreshBefore) });
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

    private IQueryable<AccountingConnection> PullOwned(int id, Guid token) =>
        _db.AccountingConnections.IgnoreQueryFilters().Where(row => row.Id == id && row.PullClaimToken == token);
    private IQueryable<AccountingConnection> RotationOwned(int id, Guid token) =>
        _db.AccountingConnections.IgnoreQueryFilters().Where(row => row.Id == id
            && row.TokenRotationState == AccountingTokenRotationState.InFlight
            && row.TokenRotationClaimToken == token);

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
