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
            WHERE loan."Id" = ANY({loanIds})
              AND loan."DeletedAt" IS NULL
              AND loan."WorkerClaimToken" = {claimToken}
              AND loan."WorkerClaimExpiresAtUtc" > clock_timestamp()
              AND loan."Status" = {(int)LoanStatus.Active}
              AND loan."TermMonths" > 0
              AND date_trunc('month', loan."StartDate") <= date_trunc('month', {AsUtc(businessDateUtc)})
            ORDER BY loan."StartDate", loan."Id"
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
