using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Automation;

public sealed record ScheduledAutomationClaim(int Id, int PortfolioId, Guid ClaimToken);

public interface IScheduledAutomationClaimStore
{
    Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimDebtServiceAsync(
        string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringExpensesAsync(
        string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringMaintenanceAsync(
        string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL claim boundary for scheduled financial and maintenance automation. Eligibility,
/// ordering, paging, expiry reclaim, row locking, and lease assignment happen in one statement.
/// Work is committed later only while the returned id/token still owns the source row.
/// </summary>
public sealed class ScheduledAutomationClaimStore : IScheduledAutomationClaimStore
{
    internal static string DebtClaimStatement => DebtSql;

    private const string DebtSql = """
        WITH candidates AS (
            SELECT loan."Id"
            FROM "Loans" AS loan
            LEFT JOIN LATERAL (
                SELECT MAX(payment."PeriodKey") AS last_period
                FROM "LoanPayments" AS payment
                WHERE payment."LoanId" = loan."Id"
            ) AS tail ON TRUE
            CROSS JOIN LATERAL (
                SELECT
                    date_trunc('month', loan."StartDate") AS start_month,
                    GREATEST(
                        loan."StartDate",
                        COALESCE(loan."DebtServiceAutomationStartDate", loan."CreatedAt")) AS activation_at,
                    GREATEST(loan."DayOfMonthDue", 1) AS due_day
            ) AS base
            CROSS JOIN LATERAL (
                SELECT
                    date_trunc('month', base.activation_at) AS activation_month,
                    date_trunc('month', @today) AS today_month
            ) AS months
            CROSS JOIN LATERAL (
                SELECT
                    months.activation_month + make_interval(days =>
                        LEAST(
                            base.due_day,
                            EXTRACT(DAY FROM months.activation_month + interval '1 month - 1 day')::int) - 1) AS activation_due,
                    months.today_month + make_interval(days =>
                        LEAST(
                            base.due_day,
                            EXTRACT(DAY FROM months.today_month + interval '1 month - 1 day')::int) - 1) AS today_due
            ) AS due_dates
            CROSS JOIN LATERAL (
                SELECT
                    CASE
                        WHEN due_dates.activation_due < base.activation_at::date
                            THEN months.activation_month + interval '1 month'
                        ELSE months.activation_month
                    END AS first_month,
                    CASE
                        WHEN due_dates.today_due > @today::date
                            THEN months.today_month - interval '1 month'
                        ELSE months.today_month
                    END AS due_through_month
            ) AS bounds
            CROSS JOIN LATERAL (
                SELECT
                    CASE
                        WHEN tail.last_period IS NULL THEN bounds.first_month
                        ELSE GREATEST(to_date(tail.last_period, 'YYYY-MM') + interval '1 month', bounds.first_month)
                    END AS next_month,
                    LEAST(
                        bounds.due_through_month,
                        base.start_month + make_interval(months => loan."TermMonths" - 1)) AS last_month
            ) AS schedule
            WHERE loan."DeletedAt" IS NULL
              AND loan."Status" = @activeStatus
              AND loan."TermMonths" > 0
              AND schedule.next_month >= base.start_month
              AND schedule.next_month <= schedule.last_month
              AND (loan."WorkerClaimToken" IS NULL OR loan."WorkerClaimExpiresAtUtc" <= clock_timestamp())
            ORDER BY schedule.next_month, loan."Id"
            FOR UPDATE OF loan SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "Loans" AS loan
        SET "WorkerClaimOwner" = @owner,
            "WorkerClaimToken" = @token,
            "WorkerClaimExpiresAtUtc" = clock_timestamp() + @leaseDuration,
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
              AND (template."WorkerClaimToken" IS NULL OR template."WorkerClaimExpiresAtUtc" <= clock_timestamp())
            ORDER BY template."NextRunDate", template."Id"
            FOR UPDATE OF template SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "RecurringExpenses" AS template
        SET "WorkerClaimOwner" = @owner,
            "WorkerClaimToken" = @token,
            "WorkerClaimExpiresAtUtc" = clock_timestamp() + @leaseDuration,
            "WorkerClaimAttemptCount" = template."WorkerClaimAttemptCount" + 1
        FROM candidates
        WHERE template."Id" = candidates."Id"
        RETURNING template."Id", template."PortfolioId", template."WorkerClaimToken";
        """;

    private const string MaintenanceSql = """
        WITH candidates AS (
            SELECT task."Id"
            FROM "RecurringMaintenanceTasks" AS task
            LEFT JOIN "AutomationSettings" AS settings ON settings."PortfolioId" = task."PortfolioId"
            WHERE task."DeletedAt" IS NULL
              AND task."IsActive"
              AND task."NextDueDate" <= @today
              AND COALESCE(settings."EnableRecurringMaintenance", TRUE)
              AND (task."WorkerClaimToken" IS NULL OR task."WorkerClaimExpiresAtUtc" <= clock_timestamp())
            ORDER BY task."NextDueDate", task."Id"
            FOR UPDATE OF task SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "RecurringMaintenanceTasks" AS task
        SET "WorkerClaimOwner" = @owner,
            "WorkerClaimToken" = @token,
            "WorkerClaimExpiresAtUtc" = clock_timestamp() + @leaseDuration,
            "WorkerClaimAttemptCount" = task."WorkerClaimAttemptCount" + 1
        FROM candidates
        WHERE task."Id" = candidates."Id"
        RETURNING task."Id", task."PortfolioId", task."WorkerClaimToken";
        """;

    private readonly RentalCommandDbContext _db;

    public ScheduledAutomationClaimStore(RentalCommandDbContext db) => _db = db;

    public Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimDebtServiceAsync(
        string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimAsync(DebtSql, owner, todayUtc, leaseDuration, batchSize, true, ct);

    public Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringExpensesAsync(
        string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimAsync(ExpenseSql, owner, todayUtc, leaseDuration, batchSize, false, ct);

    public Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringMaintenanceAsync(
        string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default) =>
        ClaimAsync(MaintenanceSql, owner, todayUtc, leaseDuration, batchSize, false, ct);


    private async Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimAsync(
        string sql, string owner, DateTime todayUtc, TimeSpan leaseDuration,
        int batchSize, bool addActiveStatus, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 200) throw new ArgumentOutOfRangeException(nameof(owner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await _db.Database.OpenConnectionAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("today", NpgsqlDbType.TimestampTz) { Value = AsUtc(todayUtc) });
            command.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Text) { Value = owner });
            command.Parameters.Add(new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
            command.Parameters.Add(new NpgsqlParameter("leaseDuration", NpgsqlDbType.Interval) { Value = leaseDuration });
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

}
