using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class VendorEndpoints
{
    public static WebApplication MapVendorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/vendors");

        group.MapGet("/", async (int portfolioId, LifecycleDbContext db) =>
        {
            var vendors = await db.Vendors
                .Where(v => v.PortfolioId == portfolioId)
                .OrderBy(v => v.Name)
                .ToListAsync();

            return Results.Ok(vendors);
        });

        group.MapPost("/", async (CreateVendorRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var now = DateTime.UtcNow;
            var vendor = new Vendor
            {
                PortfolioId = req.PortfolioId,
                Name = req.Name,
                ServiceType = req.ServiceType,
                Email = req.Email,
                Phone = req.Phone,
                TaxId = req.TaxId,
                Is1099Eligible = req.Is1099Eligible,
                W9OnFile = req.W9OnFile,
                Preferred = req.Preferred,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Vendors.Add(vendor);
            await db.SaveChangesAsync();
            await sse.BroadcastAsync("vendor:created", new { vendor.Id, vendor.PortfolioId });

            return Results.Created($"/api/vendors/{vendor.Id}", vendor);
        });

        group.MapPatch("/{id:int}", async (int id, UpdateVendorRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var vendor = await db.Vendors.FindAsync(id);
            if (vendor is null) return Results.NotFound();

            if (req.Name is not null) vendor.Name = req.Name;
            if (req.ServiceType is not null) vendor.ServiceType = req.ServiceType;
            if (req.Email is not null) vendor.Email = req.Email;
            if (req.Phone is not null) vendor.Phone = req.Phone;
            if (req.TaxId is not null) vendor.TaxId = req.TaxId;
            if (req.Is1099Eligible.HasValue) vendor.Is1099Eligible = req.Is1099Eligible.Value;
            if (req.W9OnFile.HasValue) vendor.W9OnFile = req.W9OnFile.Value;
            if (req.Preferred.HasValue) vendor.Preferred = req.Preferred.Value;
            if (req.Notes is not null) vendor.Notes = req.Notes;

            vendor.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await sse.BroadcastAsync("vendor:updated", new { vendor.Id, vendor.PortfolioId });

            return Results.Ok(vendor);
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var vendor = await db.Vendors.FindAsync(id);
            if (vendor is null) return Results.NotFound();
            db.Vendors.Remove(vendor);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateVendorRequest(
    int PortfolioId,
    string Name,
    string ServiceType,
    string? Email = null,
    string? Phone = null,
    string? TaxId = null,
    bool Is1099Eligible = false,
    bool W9OnFile = false,
    bool Preferred = false,
    string? Notes = null);

public record UpdateVendorRequest(
    string? Name = null,
    string? ServiceType = null,
    string? Email = null,
    string? Phone = null,
    string? TaxId = null,
    bool? Is1099Eligible = null,
    bool? W9OnFile = null,
    bool? Preferred = null,
    string? Notes = null);
