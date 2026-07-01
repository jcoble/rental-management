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
        var today = now.Date;

        // ── 1. Occupancy ─────────────────────────────────────────────────────────────────────────
        // Units have no direct PortfolioId; scope via their parent Property. Total and occupied counts
        // are computed SQL-side in a single grouped aggregate — no unit rows are loaded.
        var occupancyCounts = await _db.Units
            .AsNoTracking()
            .Where(u => u.Property!.PortfolioId == portfolioId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Occupied = g.Count(u => u.Status == UnitStatus.Occupied),
            })
            .FirstOrDefaultAsync(ct);

        var totalUnits = occupancyCounts?.Total ?? 0;
        var occupiedUnits = occupancyCounts?.Occupied ?? 0;
        var occupancyRate = totalUnits > 0
            ? Math.Round(100m * occupiedUnits / totalUnits, 1)
            : 0m;

        // ── 2. This-month rent ────────────────────────────────────────────────────────────────────
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1); // exclusive upper bound

        // Scheduled/overdue rent is limited to current leases; collected rent remains historical cash.
        // Both are computed SQL-side as conditional SUM/COUNT aggregates — no payment rows are loaded.
        var rentReceivables = await _db.Payments
            .AsNoTracking()
            .ForCurrentLeaseAttention(now)
            .Where(p => p.PortfolioId == portfolioId && p.PaymentType == PaymentType.Rent)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                MonthScheduled = g.Sum(p =>
                    p.DueDate >= monthStart && p.DueDate < monthEnd ? p.Amount : 0m),
                OverdueAmount = g.Sum(p =>
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Late || p.Status == PaymentStatus.Partial)
                    && p.DueDate.Date < today ? p.Amount : 0m),
                OverdueCount = g.Count(p =>
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Late || p.Status == PaymentStatus.Partial)
                    && p.DueDate.Date < today),
            })
            .FirstOrDefaultAsync(ct);

        var rentCollected = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.PaymentType == PaymentType.Rent)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                MonthCollected = g.Sum(p =>
                    p.Status == PaymentStatus.Paid && p.PaidDate != null
                    && p.PaidDate >= monthStart && p.PaidDate < monthEnd ? p.Amount : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var monthRentScheduled = rentReceivables?.MonthScheduled ?? 0m;
        var monthRentCollected = rentCollected?.MonthCollected ?? 0m;

        var collectionRate = monthRentScheduled > 0
            ? Math.Round(100m * monthRentCollected / monthRentScheduled, 1)
            : 0m;

        // ── 3. Overdue ────────────────────────────────────────────────────────────────────────────
        var overdue = new CountAmount(
            rentReceivables?.OverdueCount ?? 0,
            Math.Round(rentReceivables?.OverdueAmount ?? 0m, 2));

        // ── 4. 12-month trend ─────────────────────────────────────────────────────────────────────
        // Income (paid rent) and expenses for the 12-month window are each aggregated SQL-side with ONE
        // grouped-by-(year, month) query (EF translates DateTime.Year/.Month to date_part on Postgres and
        // strftime on SQLite — provider-agnostic, unlike date_trunc). The 12 fixed month slots are then
        // filled from the small grouped results, defaulting empty months to zero.
        var trendStart = monthStart.AddMonths(-11); // 12 months back, inclusive
        var trendEnd = monthEnd;                    // current month end (exclusive)

        var incomeByMonth = (await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId
                        && p.PaymentType == PaymentType.Rent
                        && p.Status == PaymentStatus.Paid
                        && p.PaidDate.HasValue
                        && p.PaidDate!.Value >= trendStart
                        && p.PaidDate!.Value < trendEnd)
            .GroupBy(p => new { p.PaidDate!.Value.Year, p.PaidDate!.Value.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Total = g.Sum(p => p.Amount) })
            .ToListAsync(ct))
            .ToDictionary(x => (x.Year, x.Month), x => x.Total);

        var expensesByMonth = (await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId
                        && e.IncurredAt >= trendStart
                        && e.IncurredAt < trendEnd)
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
        var expiryCounts = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                D30 = g.Count(l => l.EndDate.Date >= today && l.EndDate.Date <= day30),
                D60 = g.Count(l => l.EndDate.Date >= today && l.EndDate.Date <= day60),
                D90 = g.Count(l => l.EndDate.Date >= today && l.EndDate.Date <= day90),
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
            .ToListAsync(ct);

        var openWorkOrders = openWorkOrderGroups
            .OrderBy(g => g.Priority)
            .Select(g => new PriorityCount(g.Priority.ToString(), g.Count))
            .ToList();

        // ── 7. Monthly recurring rent ─────────────────────────────────────────────────────────────
        var monthlyRecurringRent = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active)
            .SumAsync(l => l.MonthlyRent, ct);

        return new AnalyticsOverview
        {
            TotalUnits = totalUnits,
            OccupiedUnits = occupiedUnits,
            OccupancyRate = occupancyRate,
            MonthRentScheduled = Math.Round(monthRentScheduled, 2),
            MonthRentCollected = Math.Round(monthRentCollected, 2),
            CollectionRate = collectionRate,
            Overdue = overdue,
            Trend = trend,
            LeasesExpiring30 = leasesExpiring30,
            LeasesExpiring60 = leasesExpiring60,
            LeasesExpiring90 = leasesExpiring90,
            OpenWorkOrders = openWorkOrders,
            MonthlyRecurringRent = Math.Round(monthlyRecurringRent, 2),
        };
    }
}
