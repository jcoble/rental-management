using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class PortfolioEndpoints
{
    public static WebApplication MapPortfolioEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/portfolios");

        group.MapGet("/", async (LifecycleDbContext db) =>
        {
            var items = await db.Portfolios
                .OrderByDescending(p => p.UpdatedAt)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Description,
                    p.ManagementCompanyName,
                    p.TimeZone,
                    Status = p.Status.ToString(),
                    p.Settings,
                    PropertyCount = p.Properties.Count,
                    UnitCount = p.Properties.SelectMany(pr => pr.Units).Count(),
                    ActiveLeaseCount = p.Leases.Count(l => l.Status == LeaseStatus.Active),
                    p.CreatedAt,
                    p.UpdatedAt
                })
                .ToListAsync();

            return Results.Ok(items);
        });

        group.MapPost("/", async (CreatePortfolioRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var now = DateTime.UtcNow;
            var portfolio = new Portfolio
            {
                Name = req.Name,
                Description = req.Description,
                ManagementCompanyName = string.IsNullOrWhiteSpace(req.ManagementCompanyName)
                    ? req.Name
                    : req.ManagementCompanyName,
                TimeZone = string.IsNullOrWhiteSpace(req.TimeZone) ? "America/New_York" : req.TimeZone,
                Status = PortfolioStatus.Active,
                Settings = req.Settings,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Portfolios.Add(portfolio);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                portfolio.Id,
                RentalActivityType.PortfolioCreated,
                "Portfolio",
                portfolio.Id,
                "Created",
                $"Portfolio '{portfolio.Name}' created",
                "manual");

            await sse.BroadcastAsync("portfolio:created", new { portfolio.Id, portfolio.Name });

            return Results.Created($"/api/portfolios/{portfolio.Id}", new
            {
                portfolio.Id,
                portfolio.Name,
                portfolio.Description,
                portfolio.ManagementCompanyName,
                portfolio.TimeZone,
                Status = portfolio.Status.ToString(),
                portfolio.Settings,
                portfolio.CreatedAt,
                portfolio.UpdatedAt
            });
        });

        group.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var portfolio = await db.Portfolios
                .Include(p => p.Properties)
                .ThenInclude(pr => pr.Units)
                .Include(p => p.Leases)
                .FirstOrDefaultAsync(p => p.Id == id);

            return portfolio is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    portfolio.Id,
                    portfolio.Name,
                    portfolio.Description,
                    portfolio.ManagementCompanyName,
                    portfolio.TimeZone,
                    Status = portfolio.Status.ToString(),
                    portfolio.Settings,
                    PropertyCount = portfolio.Properties.Count,
                    UnitCount = portfolio.Properties.SelectMany(pr => pr.Units).Count(),
                    ActiveLeaseCount = portfolio.Leases.Count(l => l.Status == LeaseStatus.Active),
                    portfolio.CreatedAt,
                    portfolio.UpdatedAt
                });
        });

        group.MapPatch("/{id:int}", async (int id, UpdatePortfolioRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var portfolio = await db.Portfolios.FindAsync(id);
            if (portfolio is null) return Results.NotFound();

            if (req.Name is not null) portfolio.Name = req.Name;
            if (req.Description is not null) portfolio.Description = req.Description;
            if (req.ManagementCompanyName is not null) portfolio.ManagementCompanyName = req.ManagementCompanyName;
            if (req.TimeZone is not null) portfolio.TimeZone = req.TimeZone;
            if (req.Settings is not null) portfolio.Settings = req.Settings;
            if (req.Status.HasValue) portfolio.Status = req.Status.Value;

            portfolio.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await sse.BroadcastAsync("portfolio:updated", new { portfolio.Id });

            return Results.Ok(new
            {
                portfolio.Id,
                portfolio.Name,
                portfolio.Description,
                portfolio.ManagementCompanyName,
                portfolio.TimeZone,
                Status = portfolio.Status.ToString(),
                portfolio.Settings,
                portfolio.CreatedAt,
                portfolio.UpdatedAt
            });
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var portfolio = await db.Portfolios.FindAsync(id);
            if (portfolio is null) return Results.NotFound();
            db.Portfolios.Remove(portfolio);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapGet("/{id:int}/dashboard", async (int id, LifecycleDbContext db) =>
        {
            var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.Id == id);
            if (portfolio is null) return Results.NotFound();

            var now = DateTime.UtcNow;
            var properties = await db.Properties.Where(p => p.PortfolioId == id)
                .Include(p => p.Units)
                .ToListAsync();
            var leases = await db.Leases.Where(l => l.PortfolioId == id)
                .Include(l => l.Tenant)
                .Include(l => l.Unit)
                .Include(l => l.Property)
                .ToListAsync();
            var payments = await db.Payments.Where(p => p.PortfolioId == id).ToListAsync();
            var expenses = await db.Expenses.Where(e => e.PortfolioId == id).ToListAsync();
            var workOrders = await db.WorkOrders.Where(w => w.PortfolioId == id).ToListAsync();
            var appointments = await db.Appointments.Where(a => a.PortfolioId == id).ToListAsync();
            var activities = await db.ActivityLogs.Where(a => a.PortfolioId == id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(20)
                .ToListAsync();

            var units = properties.SelectMany(p => p.Units).ToList();
            var activeLeases = leases.Where(l => l.Status == LeaseStatus.Active).ToList();
            var expiringLeases = activeLeases
                .Where(l => l.EndDate <= now.AddDays(60) && l.EndDate >= now)
                .OrderBy(l => l.EndDate)
                .Take(10)
                .Select(l => new
                {
                    l.Id,
                    l.LeaseNumber,
                    Tenant = l.Tenant is null ? null : $"{l.Tenant.FirstName} {l.Tenant.LastName}",
                    Unit = l.Unit?.UnitNumber,
                    Property = l.Property?.Name,
                    l.EndDate,
                    l.MonthlyRent
                });

            var openWorkOrders = workOrders
                .Where(w => w.Status is WorkOrderStatus.New or WorkOrderStatus.Scheduled or WorkOrderStatus.InProgress or WorkOrderStatus.WaitingParts)
                .ToList();

            var overdueAmount = payments
                .Where(p => p.Status != PaymentStatus.Paid && p.DueDate < now)
                .Sum(p => p.Amount);

            var dueThisMonthAmount = payments
                .Where(p => p.DueDate.Month == now.Month && p.DueDate.Year == now.Year)
                .Sum(p => p.Amount);

            var paidThisMonthAmount = payments
                .Where(p => p.PaidDate.HasValue && p.PaidDate.Value.Month == now.Month && p.PaidDate.Value.Year == now.Year)
                .Sum(p => p.Amount);

            var expensesThisMonthAmount = expenses
                .Where(e => e.IncurredAt.Month == now.Month && e.IncurredAt.Year == now.Year)
                .Sum(e => e.Amount);

            var upcomingAppointments = appointments
                .Where(a => a.ScheduledStart >= now && a.Status is AppointmentStatus.Scheduled or AppointmentStatus.Confirmed)
                .OrderBy(a => a.ScheduledStart)
                .Take(10)
                .Select(a => new
                {
                    a.Id,
                    a.Title,
                    Type = a.Type.ToString(),
                    Status = a.Status.ToString(),
                    a.ScheduledStart,
                    a.AssignedTo,
                    a.PropertyId,
                    a.UnitId
                });

            return Results.Ok(new
            {
                Portfolio = new
                {
                    portfolio.Id,
                    portfolio.Name,
                    portfolio.ManagementCompanyName,
                    Status = portfolio.Status.ToString(),
                    portfolio.TimeZone
                },
                Occupancy = new
                {
                    TotalUnits = units.Count,
                    OccupiedUnits = units.Count(u => u.Status == UnitStatus.Occupied),
                    VacantUnits = units.Count(u => u.Status == UnitStatus.Vacant),
                    ReservedUnits = units.Count(u => u.Status == UnitStatus.Reserved),
                    OccupancyRate = units.Count == 0 ? 0 : Math.Round(units.Count(u => u.Status == UnitStatus.Occupied) * 100m / units.Count, 1)
                },
                Leasing = new
                {
                    TotalLeases = leases.Count,
                    ActiveLeases = activeLeases.Count,
                    ExpiringSoon = expiringLeases,
                    ByStatus = leases
                        .GroupBy(l => l.Status.ToString())
                        .ToDictionary(g => g.Key, g => g.Count())
                },
                Accounting = new
                {
                    DueThisMonthAmount = dueThisMonthAmount,
                    PaidThisMonthAmount = paidThisMonthAmount,
                    OverdueAmount = overdueAmount,
                    ExpensesThisMonthAmount = expensesThisMonthAmount,
                    NetThisMonth = paidThisMonthAmount - expensesThisMonthAmount
                },
                Maintenance = new
                {
                    OpenCount = openWorkOrders.Count,
                    EmergencyCount = openWorkOrders.Count(w => w.Priority == WorkOrderPriority.Emergency),
                    InProgressCount = openWorkOrders.Count(w => w.Status == WorkOrderStatus.InProgress)
                },
                UpcomingAppointments = upcomingAppointments,
                RecentActivity = activities.Select(a => new
                {
                    a.Id,
                    Type = a.Type.ToString(),
                    a.EntityType,
                    a.EntityId,
                    a.Action,
                    a.Description,
                    a.Actor,
                    a.CreatedAt
                })
            });
        });

        return app;
    }
}

public record CreatePortfolioRequest(
    string Name,
    string? Description,
    string? ManagementCompanyName,
    string? TimeZone,
    string? Settings);

public record UpdatePortfolioRequest(
    string? Name,
    string? Description,
    string? ManagementCompanyName,
    string? TimeZone,
    PortfolioStatus? Status,
    string? Settings);
