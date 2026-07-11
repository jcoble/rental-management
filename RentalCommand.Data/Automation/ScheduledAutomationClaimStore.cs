using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Automation;

public sealed record ScheduledAutomationClaim(int Id, int PortfolioId, Guid ClaimToken);
public sealed record LoanPaymentTail(int LoanId, string PeriodKey, decimal BalanceAfter);

public interface IScheduledAutomationClaimStore
{
    Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimDebtServiceAsync(
        string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringExpensesAsync(
        string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringMaintenanceAsync(
        string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<Loan>> LockOwnedLoansAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims, DateTime todayUtc, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, LoanPaymentTail>> LoadLoanTailsAsync(
        IReadOnlyList<int> loanIds, CancellationToken ct = default);
    Task<IReadOnlyList<RecurringExpense>> LockOwnedRecurringExpensesAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims, DateTime todayUtc, CancellationToken ct = default);
    Task<IReadOnlyList<RecurringMaintenanceTask>> LockOwnedRecurringMaintenanceAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims, DateTime todayUtc, CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL claim boundary for scheduled financial and maintenance automation. Eligibility,
/// ordering, paging, expiry reclaim, row locking, and lease assignment happen in one statement.
/// Work is committed later only while the returned id/token still owns the source row.
/// </summary>
public sealed class ScheduledAutomationClaimStore : IScheduledAutomationClaimStore
{
    private const string DebtSql = """
        WITH candidates AS (
            SELECT loan."Id"
            FROM "Loans" AS loan
            WHERE loan."DeletedAt" IS NULL
              AND loan."Status" = @activeStatus
              AND loan."TermMonths" > 0
              AND date_trunc('month', loan."StartDate") <= date_trunc('month', @today)
              AND (loan."WorkerClaimToken" IS NULL OR loan."WorkerClaimExpiresAtUtc" <= @now)
              AND COALESCE((
                    SELECT MAX(payment."PeriodKey")
                    FROM "LoanPayments" AS payment
                    WHERE payment."LoanId" = loan."Id"
                  ), '') < to_char(
                    LEAST(
                      date_trunc('month', @today),
                      date_trunc('month', loan."StartDate")
                        + make_interval(months => loan."TermMonths" - 1)),
                    'YYYY-MM')
            ORDER BY loan."StartDate", loan."Id"
            FOR UPDATE OF loan SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "Loans" AS loan
        SET "WorkerClaimOwner" = @owner,
            "WorkerClaimToken" = @token,
            "WorkerClaimExpiresAtUtc" = @expires,
            "WorkerClaimAttemptCount" = loan."WorkerClaimAttemptCount" + 1
        FROM candidates
        WHERE loan."Id" = candidates."Id"
        RETURNING loan."Id", loan."PortfolioId", loan."WorkerClaimToken";
        """;

    private const string ExpenseSql = """
        WITH candidates AS (
            SELECT template."Id"
            FROM "RecurringExpenses" AS template
            WHERE template."DeletedAt" IS NULL
              AND template."Active"
              AND template."NextRunDate" <= @today
              AND (template."WorkerClaimToken" IS NULL OR template."WorkerClaimExpiresAtUtc" <= @now)
            ORDER BY template."NextRunDate", template."Id"
            FOR UPDATE OF template SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "RecurringExpenses" AS template
        SET "WorkerClaimOwner" = @owner,
            "WorkerClaimToken" = @token,
            "WorkerClaimExpiresAtUtc" = @expires,
            "WorkerClaimAttemptCount" = template."WorkerClaimAttemptCount" + 1
        FROM candidates
        WHERE template."Id" = candidates."Id"
        RETURNING template."Id", template."PortfolioId", template."WorkerClaimToken";
        """;

    private const string MaintenanceSql = """
        WITH candidates AS (
            SELECT task."Id"
            FROM "RecurringMaintenanceTasks" AS task
            LEFT JOIN "NotificationSettings" AS settings ON settings."PortfolioId" = task."PortfolioId"
            WHERE task."DeletedAt" IS NULL
              AND task."IsActive"
              AND task."NextDueDate" <= @today
              AND COALESCE(settings."EnableRecurringMaintenance", TRUE)
              AND (task."WorkerClaimToken" IS NULL OR task."WorkerClaimExpiresAtUtc" <= @now)
            ORDER BY task."NextDueDate", task."Id"
            FOR UPDATE OF task SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "RecurringMaintenanceTasks" AS task
        SET "WorkerClaimOwner" = @owner,
            "WorkerClaimToken" = @token,
            "WorkerClaimExpiresAtUtc" = @expires,
            "WorkerClaimAttemptCount" = task."WorkerClaimAttemptCount" + 1
        FROM candidates
        WHERE task."Id" = candidates."Id"
        RETURNING task."Id", task."PortfolioId", task."WorkerClaimToken";
        """;

    private readonly RentalCommandDbContext _db;

    public ScheduledAutomationClaimStore(RentalCommandDbContext db) => _db = db;

    public Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimDebtServiceAsync(
        string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimAsync(DebtSql, owner, todayUtc, nowUtc, leaseDuration, batchSize, true, ct);

    public Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringExpensesAsync(
        string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimAsync(ExpenseSql, owner, todayUtc, nowUtc, leaseDuration, batchSize, false, ct);

    public Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringMaintenanceAsync(
        string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimAsync(MaintenanceSql, owner, todayUtc, nowUtc, leaseDuration, batchSize, false, ct);

    public async Task<IReadOnlyList<Loan>> LockOwnedLoansAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims, DateTime todayUtc, CancellationToken ct = default)
    {
        var (ids, token) = BatchIdentity(claims);
        if (ids.Length == 0) return [];
        return await _db.Loans.FromSqlInterpolated($"""
            SELECT loan.*
            FROM "Loans" AS loan
            WHERE loan."Id" = ANY({ids})
              AND loan."DeletedAt" IS NULL
              AND loan."WorkerClaimToken" = {token}
              AND loan."Status" = {(int)LoanStatus.Active}
              AND loan."TermMonths" > 0
              AND date_trunc('month', loan."StartDate") <= date_trunc('month', {AsUtc(todayUtc)})
            ORDER BY loan."Id"
            FOR UPDATE OF loan
            """).IgnoreQueryFilters().ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, LoanPaymentTail>> LoadLoanTailsAsync(
        IReadOnlyList<int> loanIds, CancellationToken ct = default)
    {
        if (loanIds.Count == 0) return new Dictionary<int, LoanPaymentTail>();
        var ids = loanIds.ToArray();
        var tails = await _db.LoanPayments.AsNoTracking()
            .Where(payment => ids.Contains(payment.LoanId))
            .GroupBy(payment => payment.LoanId)
            .Select(group => group.OrderByDescending(payment => payment.PeriodKey)
                .Select(payment => new LoanPaymentTail(
                    payment.LoanId, payment.PeriodKey, payment.BalanceAfter))
                .First())
            .ToListAsync(ct);
        return tails.ToDictionary(tail => tail.LoanId);
    }

    public async Task<IReadOnlyList<RecurringExpense>> LockOwnedRecurringExpensesAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims, DateTime todayUtc, CancellationToken ct = default)
    {
        var (ids, token) = BatchIdentity(claims);
        if (ids.Length == 0) return [];
        return await _db.RecurringExpenses.FromSqlInterpolated($"""
            SELECT template.*
            FROM "RecurringExpenses" AS template
            WHERE template."Id" = ANY({ids})
              AND template."DeletedAt" IS NULL
              AND template."WorkerClaimToken" = {token}
              AND template."Active"
              AND template."NextRunDate" <= {AsUtc(todayUtc)}
            ORDER BY template."Id"
            FOR UPDATE OF template
            """).IgnoreQueryFilters().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RecurringMaintenanceTask>> LockOwnedRecurringMaintenanceAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims, DateTime todayUtc, CancellationToken ct = default)
    {
        var (ids, token) = BatchIdentity(claims);
        if (ids.Length == 0) return [];
        return await _db.RecurringMaintenanceTasks.FromSqlInterpolated($"""
            SELECT task.*
            FROM "RecurringMaintenanceTasks" AS task
            LEFT JOIN "NotificationSettings" AS settings ON settings."PortfolioId" = task."PortfolioId"
            WHERE task."Id" = ANY({ids})
              AND task."DeletedAt" IS NULL
              AND task."WorkerClaimToken" = {token}
              AND task."IsActive"
              AND task."NextDueDate" <= {AsUtc(todayUtc)}
              AND COALESCE(settings."EnableRecurringMaintenance", TRUE)
            ORDER BY task."Id"
            FOR UPDATE OF task
            """).IgnoreQueryFilters().ToListAsync(ct);
    }

    private async Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimAsync(
        string sql, string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration,
        int batchSize, bool addActiveStatus, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 200) throw new ArgumentOutOfRangeException(nameof(owner));
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
            command.Parameters.Add(new NpgsqlParameter("today", NpgsqlDbType.TimestampTz) { Value = AsUtc(todayUtc) });
            command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            command.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Text) { Value = owner });
            command.Parameters.Add(new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
            command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = now.Add(leaseDuration) });
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });
            if (addActiveStatus)
            {
                command.Parameters.Add(new NpgsqlParameter("activeStatus", NpgsqlDbType.Integer)
                {
                    Value = (int)LoanStatus.Active,
                });
            }

            var claims = new List<ScheduledAutomationClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                claims.Add(new ScheduledAutomationClaim(reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2)));
            return claims;
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static (int[] Ids, Guid Token) BatchIdentity(IReadOnlyList<ScheduledAutomationClaim> claims)
    {
        if (claims.Count == 0) return ([], Guid.Empty);
        var token = claims[0].ClaimToken;
        if (claims.Any(claim => claim.ClaimToken != token))
            throw new InvalidOperationException("A scheduled automation batch must share one claim token.");
        return (claims.Select(claim => claim.Id).ToArray(), token);
    }
}
