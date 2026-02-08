using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class TenantEndpoints
{
    public static WebApplication MapTenantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tenants");

        group.MapGet("/", async (int portfolioId, LifecycleDbContext db) =>
        {
            var tenants = await db.Tenants
                .Where(t => t.PortfolioId == portfolioId)
                .Include(t => t.Leases)
                .ThenInclude(l => l.Unit)
                .OrderBy(t => t.LastName)
                .ThenBy(t => t.FirstName)
                .ToListAsync();

            return Results.Ok(tenants.Select(t => new
            {
                t.Id,
                t.PortfolioId,
                t.FirstName,
                t.LastName,
                FullName = $"{t.FirstName} {t.LastName}",
                t.Email,
                t.Phone,
                t.EmergencyContact,
                t.DateOfBirth,
                t.Notes,
                ActiveLeaseCount = t.Leases.Count(l => l.Status == LeaseStatus.Active),
                t.CreatedAt,
                t.UpdatedAt
            }));
        });

        group.MapPost("/", async (CreateTenantRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var portfolioExists = await db.Portfolios.AnyAsync(p => p.Id == req.PortfolioId);
            if (!portfolioExists) return Results.BadRequest(new { error = "Portfolio not found" });

            var now = DateTime.UtcNow;
            var tenant = new Tenant
            {
                PortfolioId = req.PortfolioId,
                FirstName = req.FirstName,
                LastName = req.LastName,
                Email = req.Email,
                Phone = req.Phone,
                EmergencyContact = req.EmergencyContact,
                DateOfBirth = req.DateOfBirth,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                req.PortfolioId,
                RentalActivityType.TenantCreated,
                "Tenant",
                tenant.Id,
                "Created",
                $"Tenant '{tenant.FirstName} {tenant.LastName}' added",
                "manual");

            await sse.BroadcastAsync("tenant:created", new { tenant.Id, tenant.PortfolioId });
            return Results.Created($"/api/tenants/{tenant.Id}", tenant);
        });

        group.MapPatch("/{id:int}", async (int id, UpdateTenantRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var tenant = await db.Tenants.FindAsync(id);
            if (tenant is null) return Results.NotFound();

            if (req.FirstName is not null) tenant.FirstName = req.FirstName;
            if (req.LastName is not null) tenant.LastName = req.LastName;
            if (req.Email is not null) tenant.Email = req.Email;
            if (req.Phone is not null) tenant.Phone = req.Phone;
            if (req.EmergencyContact is not null) tenant.EmergencyContact = req.EmergencyContact;
            if (req.DateOfBirth.HasValue) tenant.DateOfBirth = req.DateOfBirth;
            if (req.Notes is not null) tenant.Notes = req.Notes;
            tenant.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await sse.BroadcastAsync("tenant:updated", new { tenant.Id, tenant.PortfolioId });
            return Results.Ok(tenant);
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var tenant = await db.Tenants.FindAsync(id);
            if (tenant is null) return Results.NotFound();
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateTenantRequest(
    int PortfolioId,
    string FirstName,
    string LastName,
    string? Email = null,
    string? Phone = null,
    string? EmergencyContact = null,
    DateTime? DateOfBirth = null,
    string? Notes = null);

public record UpdateTenantRequest(
    string? FirstName = null,
    string? LastName = null,
    string? Email = null,
    string? Phone = null,
    string? EmergencyContact = null,
    DateTime? DateOfBirth = null,
    string? Notes = null);
