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
        // by their global query filters.
        var units = await _db.Units
            .AsNoTracking()
            .Where(u => u.Property!.PortfolioId == portfolioId)
            .Select(u => new { u.Id, u.Status })
            .ToListAsync(ct);

        // A unit is occupied if it has an active (non-deleted) lease right now.
        var occupiedUnitIds = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active)
            .Select(l => l.UnitId)
            .Distinct()
            .ToListAsync(ct);

        var occupiedSet = occupiedUnitIds.ToHashSet();

        var totalUnits = units.Count;
        var occupiedUnits = units.Count(u => occupiedSet.Contains(u.Id));
        var reservedUnits = units.Count(u => !occupiedSet.Contains(u.Id) && u.Status == UnitStatus.Reserved);
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
        var payments = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .Select(p => new { p.Status, p.PaymentType, p.Amount, p.DueDate, p.PaidDate })
            .ToListAsync(ct);

        decimal overdue = 0;
        decimal dueThisMonth = 0;
        decimal paidThisMonth = 0;

        foreach (var p in payments)
        {
            var owed = p.Status is PaymentStatus.Scheduled or PaymentStatus.Partial or PaymentStatus.Late;

            // Overdue: still owed and past its due date (Late is overdue regardless of clock skew).
            if (owed && (p.Status == PaymentStatus.Late || p.DueDate < now))
            {
                overdue += p.Amount;
            }

            // Billed this month (anything with a due date in the current month that we still expect).
            if (p.DueDate >= monthStart && p.DueDate < nextMonthStart && p.Status != PaymentStatus.Waived)
            {
                dueThisMonth += p.Amount;
            }

            // Collected rent this month, based on when it was actually paid.
            if (p.Status == PaymentStatus.Paid
                && p.PaymentType == PaymentType.Rent
                && p.PaidDate.HasValue
                && p.PaidDate.Value >= monthStart
                && p.PaidDate.Value < nextMonthStart)
            {
                paidThisMonth += p.Amount;
            }
        }

        // Expenses incurred this month (soft-deleted expenses excluded by the global query filter).
        var expensesThisMonth = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId
                && e.IncurredAt >= monthStart
                && e.IncurredAt < nextMonthStart)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

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
        var workOrders = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId)
            .Select(w => new { w.Status, w.Priority })
            .ToListAsync(ct);

        bool IsOpen(WorkOrderStatus s) =>
            s is not (WorkOrderStatus.Completed or WorkOrderStatus.Cancelled or WorkOrderStatus.Archived);

        var open = workOrders.Where(w => IsOpen(w.Status)).ToList();

        return new DashboardMaintenance
        {
            OpenCount = open.Count,
            EmergencyCount = open.Count(w => w.Priority == WorkOrderPriority.Emergency),
            InProgressCount = open.Count(w => w.Status == WorkOrderStatus.InProgress),
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
