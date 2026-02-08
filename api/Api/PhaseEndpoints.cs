using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class UnitEndpoints
{
    public static WebApplication MapUnitEndpoints(this WebApplication app)
    {
        app.MapGet("/api/properties/{propertyId:int}/units", async (int propertyId, LifecycleDbContext db) =>
        {
            var units = await db.Units
                .Where(u => u.PropertyId == propertyId)
                .OrderBy(u => u.UnitNumber)
                .ToListAsync();

            return Results.Ok(units.Select(u => new
            {
                u.Id,
                u.PropertyId,
                u.UnitNumber,
                u.FloorPlan,
                u.Bedrooms,
                u.Bathrooms,
                u.SquareFeet,
                u.MarketRent,
                Status = u.Status.ToString(),
                u.Notes,
                u.CreatedAt,
                u.UpdatedAt
            }));
        });

        app.MapPost("/api/properties/{propertyId:int}/units", async (
            int propertyId,
            CreateUnitRequest req,
            LifecycleDbContext db,
            SseService sse) =>
        {
            var property = await db.Properties.FindAsync(propertyId);
            if (property is null) return Results.NotFound(new { error = "Property not found" });

            var now = DateTime.UtcNow;
            var unit = new Unit
            {
                PropertyId = propertyId,
                UnitNumber = req.UnitNumber,
                FloorPlan = req.FloorPlan,
                Bedrooms = req.Bedrooms,
                Bathrooms = req.Bathrooms,
                SquareFeet = req.SquareFeet,
                MarketRent = req.MarketRent,
                Status = req.Status ?? UnitStatus.Vacant,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Units.Add(unit);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                property.PortfolioId,
                RentalActivityType.UnitCreated,
                "Unit",
                unit.Id,
                "Created",
                $"Unit {unit.UnitNumber} added to {property.Name}",
                "manual");

            await sse.BroadcastAsync("unit:created", new { unit.Id, unit.PropertyId, property.PortfolioId });
            return Results.Created($"/api/units/{unit.Id}", unit);
        });

        app.MapPatch("/api/units/{id:int}", async (int id, UpdateUnitRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var unit = await db.Units.Include(u => u.Property).FirstOrDefaultAsync(u => u.Id == id);
            if (unit is null) return Results.NotFound();

            if (req.UnitNumber is not null) unit.UnitNumber = req.UnitNumber;
            if (req.FloorPlan is not null) unit.FloorPlan = req.FloorPlan;
            if (req.Bedrooms.HasValue) unit.Bedrooms = req.Bedrooms.Value;
            if (req.Bathrooms.HasValue) unit.Bathrooms = req.Bathrooms.Value;
            if (req.SquareFeet.HasValue) unit.SquareFeet = req.SquareFeet.Value;
            if (req.MarketRent.HasValue) unit.MarketRent = req.MarketRent.Value;
            if (req.Status.HasValue) unit.Status = req.Status.Value;
            if (req.Notes is not null) unit.Notes = req.Notes;
            unit.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await sse.BroadcastAsync("unit:updated", new { unit.Id, unit.PropertyId, PortfolioId = unit.Property?.PortfolioId });

            return Results.Ok(unit);
        });

        app.MapDelete("/api/units/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var unit = await db.Units.FindAsync(id);
            if (unit is null) return Results.NotFound();
            db.Units.Remove(unit);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateUnitRequest(
    string UnitNumber,
    decimal Bedrooms,
    decimal Bathrooms,
    decimal MarketRent,
    string? FloorPlan = null,
    int? SquareFeet = null,
    UnitStatus? Status = null,
    string? Notes = null);

public record UpdateUnitRequest(
    string? UnitNumber = null,
    string? FloorPlan = null,
    decimal? Bedrooms = null,
    decimal? Bathrooms = null,
    int? SquareFeet = null,
    decimal? MarketRent = null,
    UnitStatus? Status = null,
    string? Notes = null);
