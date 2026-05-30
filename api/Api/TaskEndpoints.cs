using RentalCommand.Data;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Api;

public static class LeaseEndpoints
{
    public static WebApplication MapLeaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/leases");

        group.MapGet("/", async (int portfolioId, string? status, RentalCommandDbContext db) =>
        {
            var query = db.Leases
                .Where(l => l.PortfolioId == portfolioId)
                .Include(l => l.Tenant)
                .Include(l => l.Property)
                .Include(l => l.Unit)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LeaseStatus>(status, out var parsed))
            {
                query = query.Where(l => l.Status == parsed);
            }

            var leases = await query.OrderByDescending(l => l.CreatedAt).ToListAsync();

            return Results.Ok(leases.Select(l => new
            {
                l.Id,
                l.PortfolioId,
                l.PropertyId,
                l.UnitId,
                l.TenantId,
                l.LeaseNumber,
                Status = l.Status.ToString(),
                l.StartDate,
                l.EndDate,
                l.MoveInDate,
                l.MoveOutDate,
                l.MonthlyRent,
                l.SecurityDeposit,
                l.LateFeeAmount,
                l.RentDueDay,
                l.Notes,
                TenantName = l.Tenant is null ? null : $"{l.Tenant.FirstName} {l.Tenant.LastName}",
                PropertyName = l.Property?.Name,
                UnitNumber = l.Unit?.UnitNumber,
                l.CreatedAt,
                l.UpdatedAt
            }));
        });

        group.MapPost("/", async (CreateLeaseRequest req, RentalCommandDbContext db, SseService sse) =>
        {
            var unit = await db.Units.Include(u => u.Property).FirstOrDefaultAsync(u => u.Id == req.UnitId);
            if (unit is null) return Results.BadRequest(new { error = "Unit not found" });
            if (unit.Property is null || unit.Property.PortfolioId != req.PortfolioId)
                return Results.BadRequest(new { error = "Unit does not belong to portfolio" });

            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == req.TenantId && t.PortfolioId == req.PortfolioId);
            if (tenant is null) return Results.BadRequest(new { error = "Tenant not found" });

            var now = DateTime.UtcNow;
            var lease = new Lease
            {
                PortfolioId = req.PortfolioId,
                PropertyId = unit.PropertyId,
                UnitId = req.UnitId,
                TenantId = req.TenantId,
                LeaseNumber = string.IsNullOrWhiteSpace(req.LeaseNumber)
                    ? $"L-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpperInvariant()}"
                    : req.LeaseNumber,
                Status = req.Status ?? LeaseStatus.Draft,
                StartDate = req.StartDate,
                EndDate = req.EndDate,
                MoveInDate = req.MoveInDate,
                MoveOutDate = req.MoveOutDate,
                MonthlyRent = req.MonthlyRent,
                SecurityDeposit = req.SecurityDeposit,
                LateFeeAmount = req.LateFeeAmount,
                RentDueDay = req.RentDueDay,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Leases.Add(lease);

            if (lease.Status == LeaseStatus.Active)
            {
                unit.Status = UnitStatus.Occupied;
                unit.UpdatedAt = now;
            }

            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                req.PortfolioId,
                RentalActivityType.LeaseCreated,
                "Lease",
                lease.Id,
                "Created",
                $"Lease '{lease.LeaseNumber}' created for {tenant.FirstName} {tenant.LastName}",
                "manual");

            await sse.BroadcastAsync("lease:created", new { lease.Id, lease.PortfolioId });

            return Results.Created($"/api/leases/{lease.Id}", lease);
        });

        group.MapPatch("/{id:int}", async (int id, UpdateLeaseRequest req, RentalCommandDbContext db, SseService sse) =>
        {
            var lease = await db.Leases.Include(l => l.Unit).FirstOrDefaultAsync(l => l.Id == id);
            if (lease is null) return Results.NotFound();

            if (req.LeaseNumber is not null) lease.LeaseNumber = req.LeaseNumber;
            if (req.StartDate.HasValue) lease.StartDate = req.StartDate.Value;
            if (req.EndDate.HasValue) lease.EndDate = req.EndDate.Value;
            if (req.MoveInDate.HasValue) lease.MoveInDate = req.MoveInDate.Value;
            if (req.MoveOutDate.HasValue) lease.MoveOutDate = req.MoveOutDate.Value;
            if (req.MonthlyRent.HasValue) lease.MonthlyRent = req.MonthlyRent.Value;
            if (req.SecurityDeposit.HasValue) lease.SecurityDeposit = req.SecurityDeposit.Value;
            if (req.LateFeeAmount.HasValue) lease.LateFeeAmount = req.LateFeeAmount.Value;
            if (req.RentDueDay.HasValue) lease.RentDueDay = req.RentDueDay.Value;
            if (req.Notes is not null) lease.Notes = req.Notes;

            if (req.Status.HasValue && req.Status.Value != lease.Status)
            {
                lease.Status = req.Status.Value;
                if (lease.Unit is not null)
                {
                    lease.Unit.Status = lease.Status == LeaseStatus.Active
                        ? UnitStatus.Occupied
                        : UnitStatus.Vacant;
                    lease.Unit.UpdatedAt = DateTime.UtcNow;
                }

                await ActivityHelper.LogActivity(
                    db,
                    lease.PortfolioId,
                    RentalActivityType.LeaseStatusChanged,
                    "Lease",
                    lease.Id,
                    "StatusChanged",
                    $"Lease '{lease.LeaseNumber}' moved to {lease.Status}",
                    "manual");
            }

            lease.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("lease:updated", new { lease.Id, lease.PortfolioId });
            return Results.Ok(lease);
        });

        group.MapDelete("/{id:int}", async (int id, RentalCommandDbContext db) =>
        {
            var lease = await db.Leases.FindAsync(id);
            if (lease is null) return Results.NotFound();
            db.Leases.Remove(lease);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapGet("/{id:int}/ledger", async (int id, RentalCommandDbContext db) =>
        {
            var lease = await db.Leases
                .Include(l => l.Payments.OrderBy(p => p.DueDate))
                .Include(l => l.Tenant)
                .Include(l => l.Property)
                .Include(l => l.Unit)
                .FirstOrDefaultAsync(l => l.Id == id);
            if (lease is null) return Results.NotFound();

            return Results.Ok(new
            {
                Lease = new
                {
                    lease.Id,
                    lease.LeaseNumber,
                    Status = lease.Status.ToString(),
                    lease.StartDate,
                    lease.EndDate,
                    lease.MonthlyRent,
                    Tenant = lease.Tenant is null ? null : $"{lease.Tenant.FirstName} {lease.Tenant.LastName}",
                    Property = lease.Property?.Name,
                    Unit = lease.Unit?.UnitNumber
                },
                Payments = lease.Payments.Select(p => new
                {
                    p.Id,
                    Type = p.PaymentType.ToString(),
                    Status = p.Status.ToString(),
                    p.Amount,
                    p.DueDate,
                    p.PaidDate,
                    p.Method,
                    p.ExternalReference,
                    p.Notes
                }),
                Summary = new
                {
                    Expected = lease.Payments.Sum(p => p.Amount),
                    Paid = lease.Payments.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.Amount),
                    Outstanding = lease.Payments.Where(p => p.Status != PaymentStatus.Paid).Sum(p => p.Amount)
                }
            });
        });

        return app;
    }
}

public record CreateLeaseRequest(
    int PortfolioId,
    int UnitId,
    int TenantId,
    DateTime StartDate,
    DateTime EndDate,
    decimal MonthlyRent,
    decimal SecurityDeposit,
    decimal LateFeeAmount,
    int RentDueDay,
    string? LeaseNumber = null,
    LeaseStatus? Status = null,
    DateTime? MoveInDate = null,
    DateTime? MoveOutDate = null,
    string? Notes = null);

public record UpdateLeaseRequest(
    string? LeaseNumber = null,
    LeaseStatus? Status = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    DateTime? MoveInDate = null,
    DateTime? MoveOutDate = null,
    decimal? MonthlyRent = null,
    decimal? SecurityDeposit = null,
    decimal? LateFeeAmount = null,
    int? RentDueDay = null,
    string? Notes = null);
