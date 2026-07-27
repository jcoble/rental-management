using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicScheduledFinancePersistence : IAtomicScheduledFinancePersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicScheduledFinancePersistence(RentalCommandDbContext db, AtomicAuditScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<IReadOnlyList<Loan>> LockDebtServiceClaimsAsync(
        int[] loanIds,
        Guid claimToken,
        DateTime businessDateUtc,
        CancellationToken ct = default)
    {
        if (loanIds.Length == 0) return [];
        using var lease = _scope.BeginInternalRawDml("Loans", AtomicRawDmlOperation.Update);
        return await _db.Loans.FromSqlInterpolated($"""
            SELECT loan.*
            FROM "Loans" AS loan
            LEFT JOIN LATERAL (
                SELECT MAX(payment."PeriodKey") AS last_period
                FROM "LoanPayments" AS payment
                WHERE payment."LoanId" = loan."Id"
            ) AS tail ON TRUE
            CROSS JOIN LATERAL (
                SELECT
                    date_trunc('month', loan."StartDate") AS start_month,
                    GREATEST(loan."StartDate", loan."CreatedAt") AS activation_at,
                    GREATEST(loan."DayOfMonthDue", 1) AS due_day
            ) AS base
            CROSS JOIN LATERAL (
                SELECT
                    date_trunc('month', base.activation_at) AS activation_month,
                    date_trunc('month', {AsUtc(businessDateUtc)}) AS today_month
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
                        WHEN due_dates.today_due > {AsUtc(businessDateUtc)}::date
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
            WHERE loan."Id" = ANY({loanIds})
              AND loan."DeletedAt" IS NULL
              AND loan."WorkerClaimToken" = {claimToken}
              AND loan."WorkerClaimExpiresAtUtc" > clock_timestamp()
              AND loan."Status" = {(int)LoanStatus.Active}
              AND loan."TermMonths" > 0
              AND schedule.next_month >= base.start_month
              AND schedule.next_month <= schedule.last_month
            ORDER BY schedule.next_month, loan."Id"
            FOR UPDATE OF loan
            """).IgnoreQueryFilters().ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, AtomicLoanPaymentTail>> LoadLoanPaymentTailsAsync(
        int[] loanIds,
        CancellationToken ct = default)
    {
        if (loanIds.Length == 0) return new Dictionary<int, AtomicLoanPaymentTail>();
        var tails = await _db.LoanPayments.AsNoTracking()
            .Where(payment => loanIds.Contains(payment.LoanId))
            .GroupBy(payment => payment.LoanId)
            .Select(group => group.OrderByDescending(payment => payment.PeriodKey)
                .Select(payment => new AtomicLoanPaymentTail(
                    payment.LoanId,
                    payment.PeriodKey,
                    payment.BalanceAfter))
                .First())
            .ToListAsync(ct);
        return tails.ToDictionary(tail => tail.LoanId);
    }

    public async Task<IReadOnlyList<RecurringExpense>> LockRecurringExpenseClaimsAsync(
        int[] recurringExpenseIds,
        Guid claimToken,
        DateTime businessDateUtc,
        CancellationToken ct = default)
    {
        if (recurringExpenseIds.Length == 0) return [];
        using var lease = _scope.BeginInternalRawDml("RecurringExpenses", AtomicRawDmlOperation.Update);
        return await _db.RecurringExpenses.FromSqlInterpolated($"""
            SELECT template.*
            FROM "RecurringExpenses" AS template
            WHERE template."Id" = ANY({recurringExpenseIds})
              AND template."DeletedAt" IS NULL
              AND template."WorkerClaimToken" = {claimToken}
              AND template."WorkerClaimExpiresAtUtc" > clock_timestamp()
              AND template."Active"
              AND template."NextRunDate" <= {AsUtc(businessDateUtc)}
            ORDER BY template."NextRunDate", template."Id"
            FOR UPDATE OF template
            """).IgnoreQueryFilters().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RecurringMaintenanceTask>> LockRecurringMaintenanceClaimsAsync(
        int[] recurringMaintenanceTaskIds,
        Guid claimToken,
        DateTime businessDateUtc,
        CancellationToken ct = default)
    {
        if (recurringMaintenanceTaskIds.Length == 0) return [];
        if (!_db.Database.IsNpgsql())
        {
            var now = DateTime.UtcNow;
            return await _db.RecurringMaintenanceTasks.IgnoreQueryFilters()
                .Where(task => recurringMaintenanceTaskIds.Contains(task.Id) &&
                    task.DeletedAt == null && task.WorkerClaimToken == claimToken &&
                    task.WorkerClaimExpiresAtUtc > now && task.IsActive &&
                    task.NextDueDate <= businessDateUtc &&
                    (!_db.AutomationSettings.Any(settings => settings.PortfolioId == task.PortfolioId) ||
                     _db.AutomationSettings.Any(settings => settings.PortfolioId == task.PortfolioId &&
                         settings.EnableRecurringMaintenance)))
                .OrderBy(task => task.Id)
                .ToListAsync(ct);
        }

        using var lease = _scope.BeginInternalRawDml(
            "RecurringMaintenanceTasks", AtomicRawDmlOperation.Update);
        return await _db.RecurringMaintenanceTasks.FromSqlInterpolated($"""
            SELECT task.*
            FROM "RecurringMaintenanceTasks" AS task
            LEFT JOIN "AutomationSettings" AS settings ON settings."PortfolioId" = task."PortfolioId"
            WHERE task."Id" = ANY({recurringMaintenanceTaskIds})
              AND task."DeletedAt" IS NULL
              AND task."WorkerClaimToken" = {claimToken}
              AND task."WorkerClaimExpiresAtUtc" > clock_timestamp()
              AND task."IsActive"
              AND task."NextDueDate" <= {AsUtc(businessDateUtc)}
              AND COALESCE(settings."EnableRecurringMaintenance", TRUE)
            ORDER BY task."Id"
            FOR UPDATE OF task
            """).IgnoreQueryFilters().ToListAsync(ct);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
