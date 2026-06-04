using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IRecurringMaintenanceTaskService"/>
public class RecurringMaintenanceTaskService : IRecurringMaintenanceTaskService
{
    private const string EntityType = "RecurringMaintenanceTask";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public RecurringMaintenanceTaskService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<RecurringMaintenanceTaskResponse>> ListAsync(int portfolioId, int? propertyId, bool? activeOnly, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.RecurringMaintenanceTasks
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId);

        if (propertyId.HasValue)
        {
            q = q.Where(t => t.PropertyId == propertyId.Value);
        }

        if (activeOnly == true)
        {
            q = q.Where(t => t.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(t =>
                EF.Functions.ILike(t.Title, $"%{term}%") ||
                (t.Description != null && EF.Functions.ILike(t.Description, $"%{term}%")) ||
                (t.Category != null && EF.Functions.ILike(t.Category, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "title" => query.SortDescending ? q.OrderByDescending(t => t.Title) : q.OrderBy(t => t.Title),
            "nextduedate" => query.SortDescending ? q.OrderByDescending(t => t.NextDueDate) : q.OrderBy(t => t.NextDueDate),
            "interval" => query.SortDescending ? q.OrderByDescending(t => t.RecurrenceInterval) : q.OrderBy(t => t.RecurrenceInterval),
            "priority" => query.SortDescending ? q.OrderByDescending(t => t.Priority) : q.OrderBy(t => t.Priority),
            "updatedat" => query.SortDescending ? q.OrderByDescending(t => t.UpdatedAt) : q.OrderBy(t => t.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
            _ => query.SortDescending ? q.OrderByDescending(t => t.NextDueDate) : q.OrderBy(t => t.NextDueDate),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(RecurringMaintenanceTaskResponse.FromEntity).ToList();
    }

    public async Task<RecurringMaintenanceTaskResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.RecurringMaintenanceTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);

        return entity == null ? null : RecurringMaintenanceTaskResponse.FromEntity(entity);
    }

    public async Task<RecurringMaintenanceTaskResponse?> CreateAsync(int portfolioId, CreateRecurringMaintenanceTaskRequest request, CancellationToken ct = default)
    {
        // Property is required and must be in-portfolio; optional unit/vendor must be too (no cross-tenant linking).
        if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId, ct))
        {
            return null;
        }

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, request.PropertyId, ct))
        {
            return null;
        }

        if (request.VendorId.HasValue &&
            !await _db.EnsureVendorInPortfolioAsync(portfolioId, request.VendorId.Value, ct))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new RecurringMaintenanceTask
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            VendorId = request.VendorId,
            Title = request.Title,
            Description = request.Description,
            Category = request.Category,
            RecurrenceInterval = request.RecurrenceInterval,
            NextDueDate = NormalizeDate(request.NextDueDate),
            IsActive = request.IsActive,
            Priority = request.Priority,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.RecurringMaintenanceTasks.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = RecurringMaintenanceTaskResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<RecurringMaintenanceTaskResponse?> UpdateAsync(int portfolioId, int id, UpdateRecurringMaintenanceTaskRequest request, CancellationToken ct = default)
    {
        var entity = await _db.RecurringMaintenanceTasks
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        // A re-pointed unit must still belong to this task's property within the portfolio.
        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, entity.PropertyId, ct))
        {
            return null;
        }

        if (request.VendorId.HasValue &&
            !await _db.EnsureVendorInPortfolioAsync(portfolioId, request.VendorId.Value, ct))
        {
            return null;
        }

        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.VendorId.HasValue) entity.VendorId = request.VendorId;
        if (request.Title != null) entity.Title = request.Title;
        if (request.Description != null) entity.Description = request.Description;
        if (request.Category != null) entity.Category = request.Category;
        if (request.RecurrenceInterval.HasValue) entity.RecurrenceInterval = request.RecurrenceInterval.Value;
        if (request.NextDueDate.HasValue) entity.NextDueDate = NormalizeDate(request.NextDueDate.Value);
        if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;
        if (request.Priority.HasValue) entity.Priority = request.Priority.Value;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = RecurringMaintenanceTaskResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<RecurringMaintenanceTaskResponse?> SetActiveAsync(int portfolioId, int id, bool isActive, CancellationToken ct = default)
    {
        var entity = await _db.RecurringMaintenanceTasks
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        entity.IsActive = isActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var response = RecurringMaintenanceTaskResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.RecurringMaintenanceTasks
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // Soft-delete: keep the chore's history; the global query filter hides it from every read.
        entity.DeletedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    /// <summary>
    /// NextDueDate is a calendar date, not an instant. Drop any time-of-day and pin Kind=Utc so Npgsql
    /// stores it cleanly (it rejects Unspecified for timestamptz columns). The worker compares it against
    /// the landlord's local "today", also a date.
    /// </summary>
    private static DateTime NormalizeDate(DateTime value) =>
        DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);
}
