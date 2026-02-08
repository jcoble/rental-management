using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class InspectionEndpoints
{
    public static WebApplication MapInspectionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/inspections");

        group.MapGet("/", async (int portfolioId, LifecycleDbContext db) =>
        {
            var items = await db.Inspections
                .Where(i => i.PortfolioId == portfolioId)
                .Include(i => i.Property)
                .Include(i => i.Unit)
                .OrderBy(i => i.ScheduledFor)
                .ToListAsync();

            return Results.Ok(items.Select(i => new
            {
                i.Id,
                i.PortfolioId,
                i.PropertyId,
                i.UnitId,
                i.LeaseId,
                Type = i.Type.ToString(),
                Status = i.Status.ToString(),
                i.ScheduledFor,
                i.CompletedAt,
                i.Outcome,
                i.Notes,
                PropertyName = i.Property?.Name,
                UnitNumber = i.Unit?.UnitNumber,
                i.CreatedAt,
                i.UpdatedAt
            }));
        });

        group.MapPost("/", async (CreateInspectionRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var now = DateTime.UtcNow;
            var inspection = new Inspection
            {
                PortfolioId = req.PortfolioId,
                PropertyId = req.PropertyId,
                UnitId = req.UnitId,
                LeaseId = req.LeaseId,
                Type = req.Type,
                Status = req.Status ?? InspectionStatus.Scheduled,
                ScheduledFor = req.ScheduledFor,
                CompletedAt = req.CompletedAt,
                Outcome = req.Outcome,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Inspections.Add(inspection);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                req.PortfolioId,
                RentalActivityType.InspectionScheduled,
                "Inspection",
                inspection.Id,
                "Created",
                $"{inspection.Type} inspection scheduled for {inspection.ScheduledFor:u}",
                "manual");

            await sse.BroadcastAsync("inspection:created", new { inspection.Id, inspection.PortfolioId });
            return Results.Created($"/api/inspections/{inspection.Id}", inspection);
        });

        group.MapPatch("/{id:int}", async (int id, UpdateInspectionRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var inspection = await db.Inspections.FindAsync(id);
            if (inspection is null) return Results.NotFound();

            if (req.PropertyId.HasValue) inspection.PropertyId = req.PropertyId.Value;
            if (req.UnitId.HasValue) inspection.UnitId = req.UnitId;
            if (req.LeaseId.HasValue) inspection.LeaseId = req.LeaseId;
            if (req.Type.HasValue) inspection.Type = req.Type.Value;
            if (req.Status.HasValue) inspection.Status = req.Status.Value;
            if (req.ScheduledFor.HasValue) inspection.ScheduledFor = req.ScheduledFor.Value;
            if (req.CompletedAt.HasValue) inspection.CompletedAt = req.CompletedAt;
            if (req.Outcome is not null) inspection.Outcome = req.Outcome;
            if (req.Notes is not null) inspection.Notes = req.Notes;
            inspection.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await sse.BroadcastAsync("inspection:updated", new { inspection.Id, inspection.PortfolioId });
            return Results.Ok(inspection);
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var inspection = await db.Inspections.FindAsync(id);
            if (inspection is null) return Results.NotFound();
            db.Inspections.Remove(inspection);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateInspectionRequest(
    int PortfolioId,
    int PropertyId,
    InspectionType Type,
    DateTime ScheduledFor,
    int? UnitId = null,
    int? LeaseId = null,
    InspectionStatus? Status = null,
    DateTime? CompletedAt = null,
    string? Outcome = null,
    string? Notes = null);

public record UpdateInspectionRequest(
    int? PropertyId = null,
    int? UnitId = null,
    int? LeaseId = null,
    InspectionType? Type = null,
    InspectionStatus? Status = null,
    DateTime? ScheduledFor = null,
    DateTime? CompletedAt = null,
    string? Outcome = null,
    string? Notes = null);
