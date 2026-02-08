using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class PropertyEndpoints
{
    public static WebApplication MapPropertyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/properties");

        group.MapGet("/", async (int portfolioId, LifecycleDbContext db) =>
        {
            var properties = await db.Properties
                .Where(p => p.PortfolioId == portfolioId)
                .Include(p => p.Owner)
                .Include(p => p.Units)
                .OrderBy(p => p.Name)
                .ToListAsync();

            return Results.Ok(properties.Select(p => new
            {
                p.Id,
                p.PortfolioId,
                p.OwnerId,
                p.Name,
                Type = p.PropertyType.ToString(),
                Status = p.Status.ToString(),
                p.AddressLine1,
                p.AddressLine2,
                p.City,
                p.State,
                p.PostalCode,
                p.YearBuilt,
                p.ManagementFeePercent,
                p.Notes,
                OwnerName = p.Owner?.Name,
                UnitCount = p.Units.Count,
                OccupiedUnits = p.Units.Count(u => u.Status == UnitStatus.Occupied),
                p.CreatedAt,
                p.UpdatedAt
            }));
        });

        group.MapPost("/", async (CreatePropertyRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var portfolioExists = await db.Portfolios.AnyAsync(p => p.Id == req.PortfolioId);
            if (!portfolioExists) return Results.BadRequest(new { error = "Portfolio not found" });

            var now = DateTime.UtcNow;
            var entity = new Property
            {
                PortfolioId = req.PortfolioId,
                OwnerId = req.OwnerId,
                Name = req.Name,
                PropertyType = req.Type,
                Status = req.Status ?? PropertyStatus.Active,
                AddressLine1 = req.AddressLine1,
                AddressLine2 = req.AddressLine2,
                City = req.City,
                State = req.State,
                PostalCode = req.PostalCode,
                YearBuilt = req.YearBuilt,
                ManagementFeePercent = req.ManagementFeePercent,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Properties.Add(entity);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                req.PortfolioId,
                RentalActivityType.PropertyCreated,
                "Property",
                entity.Id,
                "Created",
                $"Property '{entity.Name}' added",
                "manual");

            await sse.BroadcastAsync("property:created", new { entity.Id, entity.PortfolioId });
            return Results.Created($"/api/properties/{entity.Id}", entity);
        });

        group.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var entity = await db.Properties
                .Include(p => p.Owner)
                .Include(p => p.Units)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (entity is null) return Results.NotFound();

            return Results.Ok(new
            {
                entity.Id,
                entity.PortfolioId,
                entity.OwnerId,
                entity.Name,
                Type = entity.PropertyType.ToString(),
                Status = entity.Status.ToString(),
                entity.AddressLine1,
                entity.AddressLine2,
                entity.City,
                entity.State,
                entity.PostalCode,
                entity.YearBuilt,
                entity.ManagementFeePercent,
                entity.Notes,
                Owner = entity.Owner is null
                    ? null
                    : new { entity.Owner.Id, entity.Owner.Name, entity.Owner.Email, entity.Owner.Phone },
                Units = entity.Units.Select(u => new
                {
                    u.Id,
                    u.UnitNumber,
                    u.FloorPlan,
                    u.Bedrooms,
                    u.Bathrooms,
                    u.SquareFeet,
                    u.MarketRent,
                    Status = u.Status.ToString()
                }),
                entity.CreatedAt,
                entity.UpdatedAt
            });
        });

        group.MapPatch("/{id:int}", async (int id, UpdatePropertyRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var entity = await db.Properties.FindAsync(id);
            if (entity is null) return Results.NotFound();

            if (req.OwnerId.HasValue) entity.OwnerId = req.OwnerId.Value;
            if (req.Name is not null) entity.Name = req.Name;
            if (req.Type.HasValue) entity.PropertyType = req.Type.Value;
            if (req.Status.HasValue) entity.Status = req.Status.Value;
            if (req.AddressLine1 is not null) entity.AddressLine1 = req.AddressLine1;
            if (req.AddressLine2 is not null) entity.AddressLine2 = req.AddressLine2;
            if (req.City is not null) entity.City = req.City;
            if (req.State is not null) entity.State = req.State;
            if (req.PostalCode is not null) entity.PostalCode = req.PostalCode;
            if (req.YearBuilt.HasValue) entity.YearBuilt = req.YearBuilt.Value;
            if (req.ManagementFeePercent.HasValue) entity.ManagementFeePercent = req.ManagementFeePercent.Value;
            if (req.Notes is not null) entity.Notes = req.Notes;

            entity.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await sse.BroadcastAsync("property:updated", new { entity.Id, entity.PortfolioId });

            return Results.Ok(entity);
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var entity = await db.Properties.FindAsync(id);
            if (entity is null) return Results.NotFound();
            db.Properties.Remove(entity);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreatePropertyRequest(
    int PortfolioId,
    string Name,
    PropertyType Type,
    string AddressLine1,
    string City,
    string State,
    string PostalCode,
    int? OwnerId = null,
    PropertyStatus? Status = null,
    string? AddressLine2 = null,
    int? YearBuilt = null,
    decimal? ManagementFeePercent = null,
    string? Notes = null);

public record UpdatePropertyRequest(
    int? OwnerId = null,
    string? Name = null,
    PropertyType? Type = null,
    PropertyStatus? Status = null,
    string? AddressLine1 = null,
    string? AddressLine2 = null,
    string? City = null,
    string? State = null,
    string? PostalCode = null,
    int? YearBuilt = null,
    decimal? ManagementFeePercent = null,
    string? Notes = null);
