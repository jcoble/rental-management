using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAnalyticsService"/>
public class AnalyticsService : IAnalyticsService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public AnalyticsService(RentalCommandDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<AnalyticsOverview> GetOverviewAsync(int portfolioId, CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();
        var today = DateOnly.FromDateTime(now);

        // ── 1. Occupancy ─────────────────────────────────────────────────────────────────────────
        // Units have no direct PortfolioId; scope via their parent Property. Total and occupied counts
        // are computed SQL-side in a single grouped aggregate — no unit rows are loaded.
        var occupancyCounts = await _db.UnitOccupancyProjections
            .AsNoTracking()
            .Where(u => u.PortfolioId == portfolioId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Occupied = g.Count(u => u.IsOccupied),
            })
            .FirstOrDefaultAsync(ct);

        var totalUnits = occupancyCounts?.Total ?? 0;
        var occupiedUnits = occupancyCounts?.Occupied ?? 0;
        var occupancyRate = totalUnits > 0
            ? Math.Round(100m * occupiedUnits / totalUnits, 1)
            : 0m;

        // ── 2. This-month rent ────────────────────────────────────────────────────────────────────
        var monthStart = new DateOnly(now.Year, now.Month, 1);
        var monthEnd = monthStart.AddMonths(1); // exclusive upper bound

        // Scheduled/overdue rent is limited to current leases; collected rent remains historical cash.
        // Both are computed SQL-side as conditional SUM/COUNT aggregates — no payment rows are loaded.
        var rentReceivables = await _db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.EntryType == TenantLedgerEntryType.RentCharge)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                MonthScheduled = g.Sum(entry =>
                    entry.DueOn >= monthStart && entry.DueOn < monthEnd ? entry.Amount : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var overdue = await (
                from balance in _db.TenantChargeBalanceProjections.AsNoTracking()
                join entry in _db.TenantLedgerEntries.AsNoTracking()
                    on new { balance.PortfolioId, Id = balance.TenantLedgerEntryId }
                    equals new { entry.PortfolioId, entry.Id }
                where balance.PortfolioId == portfolioId
                    && balance.IsPastDue
                    && entry.EntryType == TenantLedgerEntryType.RentCharge
                group balance by 1 into grouped
                select new
                {
                    Count = grouped.Count(),
                    Amount = grouped.Sum(row => row.OpenAmount),
                })
            .FirstOrDefaultAsync(ct);

        // Collected rent is the allocation of immutable receipt credits to rent-charge debits.
        // The receipt's effective date determines the cash period; this remains one SQL statement.
        var rentCollected = await (
                from allocation in _db.TenantLedgerAllocations.AsNoTracking()
                join credit in _db.TenantLedgerEntries.AsNoTracking()
                    on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.CreditEntryId }
                    equals new { credit.PortfolioId, credit.TenantAccountId, credit.Id }
                join debit in _db.TenantLedgerEntries.AsNoTracking()
                    on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.DebitEntryId }
                    equals new { debit.PortfolioId, debit.TenantAccountId, debit.Id }
                where allocation.PortfolioId == portfolioId
                    && credit.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && debit.EntryType == TenantLedgerEntryType.RentCharge
                group new { allocation, credit } by 1 into grouped
                select new
                {
                    MonthCollected = grouped.Sum(row =>
                        row.credit.EffectiveOn >= monthStart && row.credit.EffectiveOn < monthEnd
                            ? row.allocation.Amount
                            : 0m),
                })
            .FirstOrDefaultAsync(ct);

        var monthRentScheduled = rentReceivables?.MonthScheduled ?? 0m;
        var monthRentCollected = rentCollected?.MonthCollected ?? 0m;

        var collectionRate = monthRentScheduled > 0
            ? Math.Round(100m * monthRentCollected / monthRentScheduled, 1)
            : 0m;

        // ── 3. Overdue ────────────────────────────────────────────────────────────────────────────
        var overdueSummary = new CountAmount(
            overdue?.Count ?? 0,
            Math.Round(overdue?.Amount ?? 0m, 2));

        // ── 4. 12-month trend ─────────────────────────────────────────────────────────────────────
        // Income (paid rent) and expenses for the 12-month window are each aggregated SQL-side with ONE
        // grouped-by-(year, month) query (EF translates DateTime.Year/.Month to date_part on Postgres and
        // strftime on SQLite — provider-agnostic, unlike date_trunc). The 12 fixed month slots are then
        // filled from the small grouped results, defaulting empty months to zero.
        var trendStart = monthStart.AddMonths(-11); // 12 months back, inclusive
        var trendEnd = monthEnd;                    // current month end (exclusive)

        var incomeByMonth = (await (
                from allocation in _db.TenantLedgerAllocations.AsNoTracking()
                join credit in _db.TenantLedgerEntries.AsNoTracking()
                    on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.CreditEntryId }
                    equals new { credit.PortfolioId, credit.TenantAccountId, credit.Id }
                join debit in _db.TenantLedgerEntries.AsNoTracking()
                    on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.DebitEntryId }
                    equals new { debit.PortfolioId, debit.TenantAccountId, debit.Id }
                where allocation.PortfolioId == portfolioId
                    && credit.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && debit.EntryType == TenantLedgerEntryType.RentCharge
                    && credit.EffectiveOn >= trendStart
                    && credit.EffectiveOn < trendEnd
                group allocation by new { credit.EffectiveOn.Year, credit.EffectiveOn.Month } into grouped
                select new { grouped.Key.Year, grouped.Key.Month, Total = grouped.Sum(row => row.Amount) })
            .ToListAsync(ct))
            .ToDictionary(x => (x.Year, x.Month), x => x.Total);

        var expensesByMonth = (await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId
                        && e.IncurredAt >= trendStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                        && e.IncurredAt < trendEnd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            .GroupBy(e => new { e.IncurredAt.Year, e.IncurredAt.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Total = g.Sum(e => e.Amount) })
            .ToListAsync(ct))
            .ToDictionary(x => (x.Year, x.Month), x => x.Total);

        var trend = new List<MonthlyPoint>(12);
        for (var i = 11; i >= 0; i--)
        {
            var mStart = monthStart.AddMonths(-i);
            var label = mStart.ToString("yyyy-MM");
            var key = (mStart.Year, mStart.Month);

            var income = Math.Round(incomeByMonth.GetValueOrDefault(key, 0m), 2);
            var expenses = Math.Round(expensesByMonth.GetValueOrDefault(key, 0m), 2);

            trend.Add(new MonthlyPoint(label, income, expenses, Math.Round(income - expenses, 2)));
        }

        // ── 5. Lease expiry ───────────────────────────────────────────────────────────────────────
        // The 30/60/90-day expiry counts are computed SQL-side as conditional COUNTs in a single grouped
        // aggregate over active leases — no lease rows are loaded.
        var day30 = today.AddDays(30);
        var day60 = today.AddDays(60);
        var day90 = today.AddDays(90);
        var expiryCounts = await (
                from status in _db.LeaseAgreementStatusProjections.AsNoTracking()
                join agreement in _db.LeaseAgreements.AsNoTracking()
                    on new { status.PortfolioId, Id = status.AgreementId }
                    equals new { agreement.PortfolioId, agreement.Id }
                where status.PortfolioId == portfolioId
                    && status.IsGoverning
                    && agreement.TermEndOn != null
                group agreement by 1 into grouped
                select new
                {
                    D30 = grouped.Count(agreement => agreement.TermEndOn >= today && agreement.TermEndOn <= day30),
                    D60 = grouped.Count(agreement => agreement.TermEndOn >= today && agreement.TermEndOn <= day60),
                    D90 = grouped.Count(agreement => agreement.TermEndOn >= today && agreement.TermEndOn <= day90),
                })
            .FirstOrDefaultAsync(ct);

        var leasesExpiring30 = expiryCounts?.D30 ?? 0;
        var leasesExpiring60 = expiryCounts?.D60 ?? 0;
        var leasesExpiring90 = expiryCounts?.D90 ?? 0;

        // ── 6. Open work orders by priority ──────────────────────────────────────────────────────
        var openWorkOrderGroups = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId
                        && w.Status != WorkOrderStatus.Completed
                        && w.Status != WorkOrderStatus.Cancelled
                        && w.Status != WorkOrderStatus.Archived)
            .GroupBy(w => w.Priority)
            .Select(g => new { Priority = g.Key, Count = g.Count() })
            .OrderBy(g => g.Priority)   // order DB-side; no in-memory sort over the GROUP BY output
            .ToListAsync(ct);

        var openWorkOrders = openWorkOrderGroups
            .Select(g => new PriorityCount(g.Priority.ToString(), g.Count))
            .ToList();

        // ── 7. Monthly recurring rent ─────────────────────────────────────────────────────────────
        var monthlyRecurringRent = await (
                from status in _db.LeaseAgreementStatusProjections.AsNoTracking()
                join agreement in _db.LeaseAgreements.AsNoTracking()
                    on new { status.PortfolioId, Id = status.AgreementId }
                    equals new { agreement.PortfolioId, agreement.Id }
                where status.PortfolioId == portfolioId && status.IsGoverning
                select agreement.BaseRentAmount +
                    (_db.LeaseAddendumFinancialEffects
                        .Where(effect => effect.PortfolioId == portfolioId
                            && effect.EffectType == LeaseAddendumFinancialEffectType.RecurringRentDelta
                            && _db.LeaseAddendumStatusProjections.Any(addendum =>
                                addendum.PortfolioId == effect.PortfolioId
                                && addendum.LeaseAddendumId == effect.LeaseAddendumId
                                && addendum.LeaseManagementId == agreement.LeaseManagementId
                                && addendum.AddendumStatus == "Active"))
                        .Sum(effect => (decimal?)effect.Amount) ?? 0m))
            .Select(amount => (decimal?)amount)
            .SumAsync(ct) ?? 0m;

        return new AnalyticsOverview
        {
            TotalUnits = totalUnits,
            OccupiedUnits = occupiedUnits,
            OccupancyRate = occupancyRate,
            MonthRentScheduled = Math.Round(monthRentScheduled, 2),
            MonthRentCollected = Math.Round(monthRentCollected, 2),
            CollectionRate = collectionRate,
            Overdue = overdueSummary,
            Trend = trend,
            LeasesExpiring30 = leasesExpiring30,
            LeasesExpiring60 = leasesExpiring60,
            LeasesExpiring90 = leasesExpiring90,
            OpenWorkOrders = openWorkOrders,
            MonthlyRecurringRent = Math.Round(monthlyRecurringRent, 2),
        };
    }
}
