using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class WorkOrderEndpoints
{
    public static WebApplication MapWorkOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/work-orders");

        group.MapGet("/", async (int portfolioId, string? status, string? priority, LifecycleDbContext db) =>
        {
            var query = db.WorkOrders
                .Where(w => w.PortfolioId == portfolioId)
                .Include(w => w.Property)
                .Include(w => w.Unit)
                .Include(w => w.Tenant)
                .Include(w => w.Vendor)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<WorkOrderStatus>(status, out var parsedStatus))
                query = query.Where(w => w.Status == parsedStatus);

            if (!string.IsNullOrWhiteSpace(priority) && Enum.TryParse<WorkOrderPriority>(priority, out var parsedPriority))
                query = query.Where(w => w.Priority == parsedPriority);

            var rows = await query.OrderByDescending(w => w.RequestedAt).ToListAsync();
            return Results.Ok(rows.Select(w => new
            {
                w.Id,
                w.PortfolioId,
                w.PropertyId,
                w.UnitId,
                w.TenantId,
                w.LeaseId,
                w.VendorId,
                w.Title,
                w.Description,
                w.Category,
                Priority = w.Priority.ToString(),
                Status = w.Status.ToString(),
                w.RequestedAt,
                w.ScheduledFor,
                w.CompletedAt,
                w.EstimatedCost,
                w.ActualCost,
                w.CreatedBy,
                w.UpdatedAt,
                PropertyName = w.Property?.Name,
                UnitNumber = w.Unit?.UnitNumber,
                TenantName = w.Tenant is null ? null : $"{w.Tenant.FirstName} {w.Tenant.LastName}",
                VendorName = w.Vendor?.Name
            }));
        });

        group.MapPost("/", async (CreateWorkOrderRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == req.PropertyId && p.PortfolioId == req.PortfolioId);
            if (property is null) return Results.BadRequest(new { error = "Property not found" });

            if (req.UnitId.HasValue)
            {
                var unitExists = await db.Units.AnyAsync(u => u.Id == req.UnitId && u.PropertyId == req.PropertyId);
                if (!unitExists) return Results.BadRequest(new { error = "Unit not found for property" });
            }

            var now = DateTime.UtcNow;
            var workOrder = new WorkOrder
            {
                PortfolioId = req.PortfolioId,
                PropertyId = req.PropertyId,
                UnitId = req.UnitId,
                TenantId = req.TenantId,
                LeaseId = req.LeaseId,
                VendorId = req.VendorId,
                Title = req.Title,
                Description = req.Description,
                Category = string.IsNullOrWhiteSpace(req.Category) ? "General" : req.Category,
                Priority = req.Priority ?? WorkOrderPriority.Normal,
                Status = req.Status ?? WorkOrderStatus.New,
                RequestedAt = req.RequestedAt ?? now,
                ScheduledFor = req.ScheduledFor,
                EstimatedCost = req.EstimatedCost,
                ActualCost = req.ActualCost,
                CreatedBy = req.CreatedBy,
                UpdatedAt = now
            };

            db.WorkOrders.Add(workOrder);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                req.PortfolioId,
                RentalActivityType.WorkOrderCreated,
                "WorkOrder",
                workOrder.Id,
                "Created",
                $"Work order '{workOrder.Title}' created",
                req.CreatedBy ?? "manual");

            await sse.BroadcastAsync("work-order:created", new { workOrder.Id, workOrder.PortfolioId });
            return Results.Created($"/api/work-orders/{workOrder.Id}", workOrder);
        });

        group.MapPatch("/{id:int}", async (int id, UpdateWorkOrderRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var item = await db.WorkOrders.FindAsync(id);
            if (item is null) return Results.NotFound();

            if (req.TenantId.HasValue) item.TenantId = req.TenantId;
            if (req.LeaseId.HasValue) item.LeaseId = req.LeaseId;
            if (req.VendorId.HasValue) item.VendorId = req.VendorId;
            if (req.Title is not null) item.Title = req.Title;
            if (req.Description is not null) item.Description = req.Description;
            if (req.Category is not null) item.Category = req.Category;
            if (req.Priority.HasValue) item.Priority = req.Priority.Value;
            if (req.Status.HasValue) item.Status = req.Status.Value;
            if (req.ScheduledFor.HasValue) item.ScheduledFor = req.ScheduledFor;
            if (req.CompletedAt.HasValue) item.CompletedAt = req.CompletedAt;
            if (req.EstimatedCost.HasValue) item.EstimatedCost = req.EstimatedCost;
            if (req.ActualCost.HasValue) item.ActualCost = req.ActualCost;
            if (req.CreatedBy is not null) item.CreatedBy = req.CreatedBy;
            item.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await sse.BroadcastAsync("work-order:updated", new { item.Id, item.PortfolioId });

            return Results.Ok(item);
        });

        group.MapPost("/{id:int}/status", async (int id, UpdateWorkOrderStatusRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var item = await db.WorkOrders.FindAsync(id);
            if (item is null) return Results.NotFound();

            item.Status = req.Status;
            if (req.Status == WorkOrderStatus.Completed)
                item.CompletedAt = DateTime.UtcNow;
            item.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                item.PortfolioId,
                RentalActivityType.WorkOrderStatusChanged,
                "WorkOrder",
                item.Id,
                "StatusChanged",
                $"Work order '{item.Title}' moved to {item.Status}",
                "manual");

            await sse.BroadcastAsync("work-order:updated", new { item.Id, item.PortfolioId });
            return Results.Ok(item);
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var item = await db.WorkOrders.FindAsync(id);
            if (item is null) return Results.NotFound();
            db.WorkOrders.Remove(item);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateWorkOrderRequest(
    int PortfolioId,
    int PropertyId,
    string Title,
    string Description,
    int? UnitId = null,
    int? TenantId = null,
    int? LeaseId = null,
    int? VendorId = null,
    string? Category = null,
    WorkOrderPriority? Priority = null,
    WorkOrderStatus? Status = null,
    DateTime? RequestedAt = null,
    DateTime? ScheduledFor = null,
    decimal? EstimatedCost = null,
    decimal? ActualCost = null,
    string? CreatedBy = null);

public record UpdateWorkOrderRequest(
    int? TenantId = null,
    int? LeaseId = null,
    int? VendorId = null,
    string? Title = null,
    string? Description = null,
    string? Category = null,
    WorkOrderPriority? Priority = null,
    WorkOrderStatus? Status = null,
    DateTime? ScheduledFor = null,
    DateTime? CompletedAt = null,
    decimal? EstimatedCost = null,
    decimal? ActualCost = null,
    string? CreatedBy = null);

public record UpdateWorkOrderStatusRequest(WorkOrderStatus Status);
