using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDashboardService"/>
public class DashboardService : IDashboardService
{
    private readonly RentalCommandDbContext _db;
    private readonly AuditDescriber _auditDescriber;

    public DashboardService(RentalCommandDbContext db, AuditDescriber auditDescriber)
    {
        _db = db;
        _auditDescriber = auditDescriber;
    }

    public async Task<DashboardResponse?> GetDashboardAsync(int portfolioId, CancellationToken ct = default)
    {
        var portfolio = await _db.Portfolios
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);
        if (portfolio == null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStart = monthStart.AddMonths(1);
        var soonCutoff = now.AddDays(60);

        return new DashboardResponse
        {
            Portfolio = new DashboardPortfolio
            {
                Id = portfolio.Id,
                Name = portfolio.Name,
                ManagementCompanyName = string.IsNullOrWhiteSpace(portfolio.ManagementCompanyName)
                    ? "Rental Command"
                    : portfolio.ManagementCompanyName,
                TimeZone = string.IsNullOrWhiteSpace(portfolio.TimeZone)
                    ? "America/New_York"
                    : portfolio.TimeZone,
                Status = portfolio.Status.ToString(),
            },
            Occupancy = await BuildOccupancyAsync(portfolioId, ct),
            Accounting = await BuildAccountingAsync(portfolioId, now, monthStart, nextMonthStart, ct),
            Maintenance = await BuildMaintenanceAsync(portfolioId, ct),
            Leasing = await BuildLeasingAsync(portfolioId, now, soonCutoff, ct),
            RecentActivity = await BuildRecentActivityAsync(portfolioId, ct),
            UpcomingAppointments = await BuildUpcomingAppointmentsAsync(portfolioId, now, ct),
        };
    }

    private async Task<DashboardOccupancy> BuildOccupancyAsync(int portfolioId, CancellationToken ct)
    {
        // Units belong to the portfolio via their property. Soft-deleted units/properties are excluded
        // by their global query filters. "Occupied" is defined as Unit.Status == Occupied — the single
        // occupancy definition shared by Analytics, the Occupancy report, and the Properties list. (Do
        // NOT derive occupancy from active leases here: that diverged from the rest of the app.)
        // "Reserved" is Unit.Status == Reserved; "Vacant" is the remainder (Vacant + Offline). All counts
        // are computed SQL-side in a single grouped aggregate so no unit rows are pulled into memory.
        var counts = await _db.Units
            .AsNoTracking()
            .Where(u => u.Property!.PortfolioId == portfolioId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Occupied = g.Count(u => u.Status == UnitStatus.Occupied),
                Reserved = g.Count(u => u.Status == UnitStatus.Reserved),
            })
            .FirstOrDefaultAsync(ct);

        var totalUnits = counts?.Total ?? 0;
        var occupiedUnits = counts?.Occupied ?? 0;
        var reservedUnits = counts?.Reserved ?? 0;
        var vacantUnits = totalUnits - occupiedUnits - reservedUnits;

        var occupancyRate = totalUnits == 0
            ? 0
            : Math.Round((double)occupiedUnits / totalUnits * 100, 1);

        return new DashboardOccupancy
        {
            TotalUnits = totalUnits,
            OccupiedUnits = occupiedUnits,
            VacantUnits = vacantUnits,
            ReservedUnits = reservedUnits,
            OccupancyRate = occupancyRate,
        };
    }

    private async Task<DashboardAccounting> BuildAccountingAsync(
        int portfolioId, DateTime now, DateTime monthStart, DateTime nextMonthStart, CancellationToken ct)
    {
        // All three money figures are computed SQL-side over the whole payment set as conditional SUMs
        // (SUM(CASE WHEN <branch> THEN Amount ELSE 0 END)) in a single grouped round-trip — no payment
        // rows are pulled into memory. The branch predicates are identical to the prior in-memory loop.
        var money = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                // Overdue: still owed (Scheduled/Partial/Late) and past its due date (Late is overdue
                // regardless of clock skew). A Partial owes only its unpaid remainder; others owe in full.
                Overdue = g.Sum(p =>
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial || p.Status == PaymentStatus.Late)
                    && (p.Status == PaymentStatus.Late || p.DueDate < now)
                        ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount)
                        : 0m),
                // Billed this month (a due date in the current month that we still expect). This is the
                // full charge that was billed, regardless of how much has been collected against it.
                DueThisMonth = g.Sum(p =>
                    p.DueDate >= monthStart && p.DueDate < nextMonthStart && p.Status != PaymentStatus.Waived
                        ? p.Amount : 0m),
                // Collected cash this month, based on when it was actually paid: Paid contributes the
                // full Amount, a Partial contributes only the collected AmountPaid. This intentionally
                // includes rent, deposits, late fees, utilities, and other tenant payments so it matches
                // the dashboard money snapshot's "Total Collected" definition.
                PaidThisMonth = g.Sum(p =>
                    (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Partial)
                    && p.PaidDate != null
                    && p.PaidDate >= monthStart
                    && p.PaidDate < nextMonthStart
                        ? (p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount)
                        : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var bankCash = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId
                && t.MatchStatus != "Removed"
                && t.PostedAt >= monthStart
                && t.PostedAt < nextMonthStart)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                UnmatchedDeposits = g.Sum(t => t.Amount > 0 && t.MatchedPaymentId == null ? t.Amount : 0m),
                UnmatchedWithdrawals = g.Sum(t => t.Amount < 0 && t.MatchedExpenseId == null ? -t.Amount : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var overdue = money?.Overdue ?? 0m;
        var dueThisMonth = money?.DueThisMonth ?? 0m;
        var paidThisMonth = (money?.PaidThisMonth ?? 0m) + (bankCash?.UnmatchedDeposits ?? 0m);

        // Expenses spent this month (paid date when present, else incurred date), matching the
        // dashboard money snapshot so the summary KPI and the detailed money card cannot diverge.
        var expenseRowsThisMonth = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId
                && (e.PaidAt ?? e.IncurredAt) >= monthStart
                && (e.PaidAt ?? e.IncurredAt) < nextMonthStart)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;
        var expensesThisMonth = expenseRowsThisMonth + (bankCash?.UnmatchedWithdrawals ?? 0m);

        return new DashboardAccounting
        {
            DueThisMonthAmount = dueThisMonth,
            PaidThisMonthAmount = paidThisMonth,
            OverdueAmount = overdue,
            ExpensesThisMonthAmount = expensesThisMonth,
            NetThisMonth = paidThisMonth - expensesThisMonth,
        };
    }

    private async Task<DashboardMaintenance> BuildMaintenanceAsync(int portfolioId, CancellationToken ct)
    {
        // "Open" = any work order that is not Completed/Cancelled/Archived. The three counts (open,
        // emergency-among-open, in-progress-among-open) are computed SQL-side: the open predicate scopes
        // the query, then a single grouped aggregate emits the conditional counts. No work-order rows are
        // pulled into memory.
        var counts = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId
                && w.Status != WorkOrderStatus.Completed
                && w.Status != WorkOrderStatus.Cancelled
                && w.Status != WorkOrderStatus.Archived)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Open = g.Count(),
                Emergency = g.Count(w => w.Priority == WorkOrderPriority.Emergency),
                InProgress = g.Count(w => w.Status == WorkOrderStatus.InProgress),
            })
            .FirstOrDefaultAsync(ct);

        return new DashboardMaintenance
        {
            OpenCount = counts?.Open ?? 0,
            EmergencyCount = counts?.Emergency ?? 0,
            InProgressCount = counts?.InProgress ?? 0,
        };
    }

    private async Task<DashboardLeasing> BuildLeasingAsync(
        int portfolioId, DateTime now, DateTime soonCutoff, CancellationToken ct)
    {
        var byStatusRaw = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId)
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byStatus = byStatusRaw.ToDictionary(g => g.Status.ToString(), g => g.Count);
        var totalLeases = byStatusRaw.Sum(g => g.Count);
        var activeLeases = byStatusRaw
            .Where(g => g.Status == LeaseStatus.Active)
            .Sum(g => g.Count);

        // Active leases ending within 60 days, with tenant/property/unit labels joined in.
        var expiring = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId
                && l.Status == LeaseStatus.Active
                && l.EndDate >= now
                && l.EndDate <= soonCutoff)
            .OrderBy(l => l.EndDate)
            .Select(l => new DashboardExpiringLease
            {
                Id = l.Id,
                LeaseNumber = string.IsNullOrWhiteSpace(l.LeaseNumber) ? $"Lease #{l.Id}" : l.LeaseNumber,
                Tenant = l.Tenant != null ? (l.Tenant.FirstName + " " + l.Tenant.LastName).Trim() : null,
                Property = l.Property != null ? l.Property.Name : null,
                Unit = l.Unit != null ? l.Unit.UnitNumber : null,
                EndDate = l.EndDate,
                MonthlyRent = l.MonthlyRent,
            })
            .ToListAsync(ct);

        return new DashboardLeasing
        {
            TotalLeases = totalLeases,
            ActiveLeases = activeLeases,
            ExpiringSoon = expiring,
            ByStatus = byStatus,
        };
    }

    private async Task<IReadOnlyList<DashboardActivity>> BuildRecentActivityAsync(int portfolioId, CancellationToken ct)
    {
        // The dashboard "recent activity" widget now reads the unified audit trail. The humanized
        // description (via AuditDescriber) is what the user sees; Action carries the operation name.
        var rows = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId)
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .Take(10)
            .ToListAsync(ct);

        return rows
            .Select(a => new DashboardActivity
            {
                Id = a.Id,
                Type = a.EntityType,
                Action = a.Operation.ToString(),
                Description = _auditDescriber.Describe(a),
                Actor = a.ActorLabel ?? (a.UserId.HasValue ? $"User #{a.UserId.Value}" : "system"),
                CreatedAt = a.Timestamp,
            })
            .ToList();
    }

    private async Task<IReadOnlyList<DashboardAppointment>> BuildUpcomingAppointmentsAsync(
        int portfolioId, DateTime now, CancellationToken ct)
    {
        var appointments = await _db.Appointments
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId
                && a.ScheduledStart >= now
                && a.Status != AppointmentStatus.Cancelled)
            .OrderBy(a => a.ScheduledStart)
            .Take(10)
            .Select(a => new
            {
                a.Id,
                a.Title,
                a.Type,
                a.Status,
                a.ScheduledStart,
                a.AssignedTo,
                a.PropertyId,
                a.UnitId,
            })
            .ToListAsync(ct);

        return appointments
            .Select(a => new DashboardAppointment
            {
                Id = a.Id,
                Title = a.Title,
                Type = a.Type.ToString(),
                Status = a.Status.ToString(),
                ScheduledStart = a.ScheduledStart,
                AssignedTo = a.AssignedTo,
                PropertyId = a.PropertyId,
                UnitId = a.UnitId,
            })
            .ToList();
    }
}
