using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IRecurringMaintenanceTaskService"/>
public class RecurringMaintenanceTaskService : IRecurringMaintenanceTaskService
{
    private const string EntityType = "RecurringMaintenanceTask";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;

    public RecurringMaintenanceTaskService(RentalCommandDbContext db, IDataUpdateService dataUpdate, TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<RecurringMaintenanceTaskResponse>> ListAsync(int portfolioId, int? propertyId, bool? activeOnly, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, propertyId, activeOnly, query, ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<RecurringMaintenanceTaskResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope,
        int? propertyId,
        bool? activeOnly,
        ListQuery query,
        CancellationToken ct = default)
    {
        var page = await ListPageAuthorizedAsync(scope, propertyId, activeOnly, query, ct);
        return page.Items;
    }

    public async Task<RecurringMaintenanceTaskListResponse> ListPageAsync(int portfolioId, int? propertyId, bool? activeOnly, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.RecurringMaintenanceTasks
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId);

        return await ListPageFromQueryAsync(q, propertyId, activeOnly, query, ct);
    }

    public Task<RecurringMaintenanceTaskListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope,
        int? propertyId,
        bool? activeOnly,
        ListQuery query,
        CancellationToken ct = default) =>
        ListPageFromQueryAsync(
            AuthorizedTasks(scope, CapabilityKeys.WorkRead, tracking: false),
            propertyId,
            activeOnly,
            query,
            ct);

    private static async Task<RecurringMaintenanceTaskListResponse> ListPageFromQueryAsync(
        IQueryable<RecurringMaintenanceTask> q,
        int? propertyId,
        bool? activeOnly,
        ListQuery query,
        CancellationToken ct)
    {

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
            "property" => query.SortDescending ? q.OrderByDescending(t => t.Property!.Name) : q.OrderBy(t => t.Property!.Name),
            "propertyname" => query.SortDescending ? q.OrderByDescending(t => t.Property!.Name) : q.OrderBy(t => t.Property!.Name),
            "nextduedate" => query.SortDescending ? q.OrderByDescending(t => t.NextDueDate) : q.OrderBy(t => t.NextDueDate),
            "interval" => query.SortDescending ? q.OrderByDescending(t => t.RecurrenceInterval) : q.OrderBy(t => t.RecurrenceInterval),
            "recurrenceinterval" => query.SortDescending ? q.OrderByDescending(t => t.RecurrenceInterval) : q.OrderBy(t => t.RecurrenceInterval),
            "priority" => query.SortDescending ? q.OrderByDescending(t => t.Priority) : q.OrderBy(t => t.Priority),
            "isactive" => query.SortDescending ? q.OrderByDescending(t => t.IsActive) : q.OrderBy(t => t.IsActive),
            "updatedat" => query.SortDescending ? q.OrderByDescending(t => t.UpdatedAt) : q.OrderBy(t => t.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
            _ => query.SortDescending ? q.OrderByDescending(t => t.NextDueDate) : q.OrderBy(t => t.NextDueDate),
        };

        var totalCount = await q.CountAsync(ct);

        var items = await ProjectResponse(q)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new RecurringMaintenanceTaskListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<RecurringMaintenanceTaskResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        return await ProjectResponse(_db.RecurringMaintenanceTasks
            .AsNoTracking()
            .Where(t => t.Id == id && t.PortfolioId == portfolioId))
            .FirstOrDefaultAsync(ct);
    }

    public Task<RecurringMaintenanceTaskResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default) =>
        ProjectResponse(AuthorizedTasks(scope, CapabilityKeys.WorkRead, tracking: false)
                .Where(task => task.Id == id))
            .FirstOrDefaultAsync(ct);

    public async Task<RecurringMaintenanceTaskResponse?> CreateAsync(int portfolioId, CreateRecurringMaintenanceTaskRequest request, CancellationToken ct = default)
        => await CreateCoreAsync(portfolioId, request, ct);

    public async Task<RecurringMaintenanceTaskResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateRecurringMaintenanceTaskRequest request,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var propertyAuthorized = await _db.Properties
                .AsNoTracking()
                .Where(property => property.Id == request.PropertyId)
                .WhereAuthorized(_db, scope, [CapabilityKeys.WorkManage], _timeProvider.UtcNow())
                .AnyAsync(innerCt);
            return propertyAuthorized
                ? await CreateCoreAsync(scope.PortfolioId, request, innerCt, broadcast: false)
                : null;
        }, ct);

        if (response is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                scope.PortfolioId, EntityType, response.Id, response, ct);
        }

        return response;
    }

    private async Task<RecurringMaintenanceTaskResponse?> CreateCoreAsync(
        int portfolioId,
        CreateRecurringMaintenanceTaskRequest request,
        CancellationToken ct,
        bool broadcast = true)
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

        var now = _timeProvider.UtcNow();
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
            ScheduledTime = request.ScheduledTime,
            EstimatedCost = request.EstimatedCost,
            IsActive = request.IsActive,
            Priority = request.Priority,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.RecurringMaintenanceTasks.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? RecurringMaintenanceTaskResponse.FromEntity(entity);
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        }
        return response;
    }

    public async Task<RecurringMaintenanceTaskResponse?> UpdateAsync(int portfolioId, int id, UpdateRecurringMaintenanceTaskRequest request, CancellationToken ct = default)
        => await UpdateCoreAsync(
            _db.RecurringMaintenanceTasks.Where(task => task.Id == id && task.PortfolioId == portfolioId),
            portfolioId,
            request,
            ct);

    public async Task<RecurringMaintenanceTaskResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateRecurringMaintenanceTaskRequest request,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(innerCt =>
            UpdateCoreAsync(
                AuthorizedTasks(scope, CapabilityKeys.WorkManage, tracking: true)
                    .Where(task => task.Id == id),
                scope.PortfolioId,
                request,
                innerCt,
                broadcast: false), ct);

        if (response is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                scope.PortfolioId, EntityType, response.Id, response, ct);
        }

        return response;
    }

    private async Task<RecurringMaintenanceTaskResponse?> UpdateCoreAsync(
        IQueryable<RecurringMaintenanceTask> tasks,
        int portfolioId,
        UpdateRecurringMaintenanceTaskRequest request,
        CancellationToken ct,
        bool broadcast = true)
    {
        var entity = await tasks.FirstOrDefaultAsync(ct);
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
        entity.ScheduledTime = request.ScheduledTime;
        entity.EstimatedCost = request.EstimatedCost;
        if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;
        if (request.Priority.HasValue) entity.Priority = request.Priority.Value;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? RecurringMaintenanceTaskResponse.FromEntity(entity);
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        }
        return response;
    }

    public async Task<RecurringMaintenanceTaskResponse?> SetActiveAsync(int portfolioId, int id, bool isActive, CancellationToken ct = default)
        => await SetActiveCoreAsync(
            _db.RecurringMaintenanceTasks.Where(task => task.Id == id && task.PortfolioId == portfolioId),
            portfolioId,
            isActive,
            ct);

    public async Task<RecurringMaintenanceTaskResponse?> SetActiveAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        bool isActive,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(innerCt =>
            SetActiveCoreAsync(
                AuthorizedTasks(scope, CapabilityKeys.WorkManage, tracking: true)
                    .Where(task => task.Id == id),
                scope.PortfolioId,
                isActive,
                innerCt,
                broadcast: false), ct);

        if (response is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                scope.PortfolioId, EntityType, response.Id, response, ct);
        }

        return response;
    }

    private async Task<RecurringMaintenanceTaskResponse?> SetActiveCoreAsync(
        IQueryable<RecurringMaintenanceTask> tasks,
        int portfolioId,
        bool isActive,
        CancellationToken ct,
        bool broadcast = true)
    {
        var entity = await tasks.FirstOrDefaultAsync(ct);
        if (entity == null)
        {
            return null;
        }

        entity.IsActive = isActive;
        entity.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? RecurringMaintenanceTaskResponse.FromEntity(entity);
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        }
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
        => await DeleteCoreAsync(
            _db.RecurringMaintenanceTasks.Where(task => task.Id == id && task.PortfolioId == portfolioId),
            portfolioId,
            id,
            ct);

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var deleted = await _db.ExecuteAuthorizedMutationAsync(innerCt =>
            DeleteCoreAsync(
                AuthorizedTasks(scope, CapabilityKeys.WorkManage, tracking: true)
                    .Where(task => task.Id == id),
                scope.PortfolioId,
                id,
                innerCt,
                broadcast: false), ct);

        if (deleted)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(scope.PortfolioId, EntityType, id, ct);
        }

        return deleted;
    }

    private async Task<bool> DeleteCoreAsync(
        IQueryable<RecurringMaintenanceTask> tasks,
        int portfolioId,
        int id,
        CancellationToken ct,
        bool broadcast = true)
    {
        var entity = await tasks.FirstOrDefaultAsync(ct);
        if (entity == null)
        {
            return false;
        }

        // Soft-delete: keep the chore's history; the global query filter hides it from every read.
        entity.DeletedAt = _timeProvider.UtcNow();
        entity.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        }
        return true;
    }

    private IQueryable<RecurringMaintenanceTask> AuthorizedTasks(
        WorkspaceReadScope scope,
        string capability,
        bool tracking)
    {
        var properties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(_db, scope, [capability], _timeProvider.UtcNow());
        var tasks = tracking
            ? _db.RecurringMaintenanceTasks.AsQueryable()
            : _db.RecurringMaintenanceTasks.AsNoTracking();
        return tasks.Where(task =>
            task.PortfolioId == scope.PortfolioId &&
            properties.Any(property =>
                property.Id == task.PropertyId && property.PortfolioId == task.PortfolioId));
    }

    /// <summary>
    /// NextDueDate is a calendar date, not an instant. Drop any time-of-day and pin Kind=Utc so Npgsql
    /// stores it cleanly (it rejects Unspecified for timestamptz columns). The worker compares it against
    /// the landlord's local "today", also a date.
    /// </summary>
    private static DateTime NormalizeDate(DateTime value) =>
        DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    private static IQueryable<RecurringMaintenanceTaskResponse> ProjectResponse(IQueryable<RecurringMaintenanceTask> query) =>
        query.Select(t => new RecurringMaintenanceTaskResponse
        {
            Id = t.Id,
            PortfolioId = t.PortfolioId,
            PropertyId = t.PropertyId,
            UnitId = t.UnitId,
            VendorId = t.VendorId,
            PropertyName = t.Property == null ? null : t.Property.Name,
            UnitNumber = t.Unit == null ? null : t.Unit.UnitNumber,
            VendorName = t.Vendor == null ? null : t.Vendor.Name,
            Title = t.Title,
            Description = t.Description,
            Category = t.Category,
            RecurrenceInterval = t.RecurrenceInterval,
            NextDueDate = t.NextDueDate,
            ScheduledTime = t.ScheduledTime,
            EstimatedCost = t.EstimatedCost,
            MonthlyEstimatedCost = t.EstimatedCost == null
                ? null
                : t.RecurrenceInterval == Core.Enums.RecurrenceInterval.Weekly
                    ? t.EstimatedCost.Value * 52m / 12m
                    : t.RecurrenceInterval == Core.Enums.RecurrenceInterval.Monthly
                        ? t.EstimatedCost.Value
                        : t.RecurrenceInterval == Core.Enums.RecurrenceInterval.Quarterly
                            ? t.EstimatedCost.Value / 3m
                            : t.RecurrenceInterval == Core.Enums.RecurrenceInterval.SemiAnnually
                                ? t.EstimatedCost.Value / 6m
                                : t.RecurrenceInterval == Core.Enums.RecurrenceInterval.Annually
                                    ? t.EstimatedCost.Value / 12m
                                    : t.EstimatedCost.Value,
            GeneratedWorkOrderCount = t.WorkOrders.Count,
            LastGeneratedWorkOrderId = t.WorkOrders
                .OrderByDescending(w => w.RequestedAt)
                .ThenByDescending(w => w.Id)
                .Select(w => (int?)w.Id)
                .FirstOrDefault(),
            LastGeneratedAtUtc = t.LastGeneratedAtUtc,
            IsActive = t.IsActive,
            Priority = t.Priority,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
        });
}
