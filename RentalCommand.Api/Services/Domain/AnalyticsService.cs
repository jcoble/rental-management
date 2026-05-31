using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAnalyticsService"/>
public class AnalyticsService : IAnalyticsService
{
    private readonly RentalCommandDbContext _db;

    public AnalyticsService(RentalCommandDbContext db)
    {
        _db = db;
    }

    public async Task<AnalyticsOverview> GetOverviewAsync(int portfolioId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var today = now.Date;

        // ── 1. Occupancy ─────────────────────────────────────────────────────────────────────────
        // Units have no direct PortfolioId; scope via their parent Property.
        var unitStatuses = await _db.Units
            .AsNoTracking()
            .Where(u => u.Property!.PortfolioId == portfolioId)
            .Select(u => u.Status)
            .ToListAsync(ct);

        var totalUnits = unitStatuses.Count;
        var occupiedUnits = unitStatuses.Count(s => s == UnitStatus.Occupied);
        var occupancyRate = totalUnits > 0
            ? Math.Round(100m * occupiedUnits / totalUnits, 1)
            : 0m;

        // ── 2. This-month rent ────────────────────────────────────────────────────────────────────
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1); // exclusive upper bound

        // Pull only the minimal columns needed for this-month and overdue calculations.
        var rentPayments = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.PaymentType == PaymentType.Rent)
            .Select(p => new { p.Status, p.Amount, p.DueDate, p.PaidDate })
            .ToListAsync(ct);

        var monthRentScheduled = rentPayments
            .Where(p => p.DueDate >= monthStart && p.DueDate < monthEnd)
            .Sum(p => p.Amount);

        var monthRentCollected = rentPayments
            .Where(p => p.Status == PaymentStatus.Paid
                        && p.PaidDate.HasValue
                        && p.PaidDate.Value >= monthStart
                        && p.PaidDate.Value < monthEnd)
            .Sum(p => p.Amount);

        var collectionRate = monthRentScheduled > 0
            ? Math.Round(100m * monthRentCollected / monthRentScheduled, 1)
            : 0m;

        // ── 3. Overdue ────────────────────────────────────────────────────────────────────────────
        var overduePayments = rentPayments
            .Where(p => p.Status is PaymentStatus.Scheduled or PaymentStatus.Late or PaymentStatus.Partial
                        && p.DueDate.Date < today)
            .ToList();

        var overdue = new CountAmount(
            overduePayments.Count,
            Math.Round(overduePayments.Sum(p => p.Amount), 2));

        // ── 4. 12-month trend ─────────────────────────────────────────────────────────────────────
        // Fetch paid rent payments and expenses for the 12-month window, then aggregate in memory.
        var trendStart = monthStart.AddMonths(-11); // 12 months back, inclusive
        var trendEnd = monthEnd;                    // current month end (exclusive)

        var paidRentInWindow = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId
                        && p.PaymentType == PaymentType.Rent
                        && p.Status == PaymentStatus.Paid
                        && p.PaidDate.HasValue
                        && p.PaidDate!.Value >= trendStart
                        && p.PaidDate!.Value < trendEnd)
            .Select(p => new { p.PaidDate, p.Amount })
            .ToListAsync(ct);

        var expensesInWindow = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId
                        && e.IncurredAt >= trendStart
                        && e.IncurredAt < trendEnd)
            .Select(e => new { e.IncurredAt, e.Amount })
            .ToListAsync(ct);

        var trend = new List<MonthlyPoint>(12);
        for (var i = 11; i >= 0; i--)
        {
            var mStart = monthStart.AddMonths(-i);
            var mEnd = mStart.AddMonths(1);
            var label = mStart.ToString("yyyy-MM");

            var income = Math.Round(
                paidRentInWindow
                    .Where(p => p.PaidDate!.Value >= mStart && p.PaidDate!.Value < mEnd)
                    .Sum(p => p.Amount), 2);

            var expenses = Math.Round(
                expensesInWindow
                    .Where(e => e.IncurredAt >= mStart && e.IncurredAt < mEnd)
                    .Sum(e => e.Amount), 2);

            trend.Add(new MonthlyPoint(label, income, expenses, Math.Round(income - expenses, 2)));
        }

        // ── 5. Lease expiry ───────────────────────────────────────────────────────────────────────
        var leaseEndDates = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active)
            .Select(l => l.EndDate)
            .ToListAsync(ct);

        var leasesExpiring30 = leaseEndDates.Count(d => d.Date >= today && d.Date <= today.AddDays(30));
        var leasesExpiring60 = leaseEndDates.Count(d => d.Date >= today && d.Date <= today.AddDays(60));
        var leasesExpiring90 = leaseEndDates.Count(d => d.Date >= today && d.Date <= today.AddDays(90));

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
