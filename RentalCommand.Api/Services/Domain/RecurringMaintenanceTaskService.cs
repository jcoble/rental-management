using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IRecurringMaintenanceTaskService"/>
public class RecurringMaintenanceTaskService : IRecurringMaintenanceTaskService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestWriteExecutor? _writes;

    public RecurringMaintenanceTaskService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IRequestWriteExecutor? writes = null)
    {
        _db = db;
        _timeProvider = timeProvider;
        _writes = writes;
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

        if (query is RecurringMaintenanceTaskListQuery { UnitId: int unitId })
        {
            q = q.Where(t => t.UnitId == unitId);
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

    public async Task<RecurringMaintenanceTaskResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateRecurringMaintenanceTaskRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = RecurringMaintenanceCrudWriteSupport.Request(
            scope, RecurringMaintenanceWriteOperation.Create, 0, request, idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            RecurringMaintenanceCrudWriteSupport.IdempotencyKey(command),
            RecurringMaintenanceCrudWriteSupport.Write(
                command, CreateRecurringMaintenanceAsync, AuthorizeReplayAsync), ct);
        return Response(outcome.Value);
    }

    public async Task<RecurringMaintenanceTaskResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateRecurringMaintenanceTaskRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = RecurringMaintenanceCrudWriteSupport.Request(
            scope, RecurringMaintenanceWriteOperation.Update, id, request, idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            RecurringMaintenanceCrudWriteSupport.IdempotencyKey(command),
            RecurringMaintenanceCrudWriteSupport.Write(
                command, UpdateRecurringMaintenanceAsync, AuthorizeReplayAsync), ct);
        return Response(outcome.Value);
    }

    public async Task<RecurringMaintenanceTaskResponse?> SetActiveAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        bool isActive,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var request = new ToggleRecurringMaintenanceTaskActiveRequest { IsActive = isActive };
        var command = RecurringMaintenanceCrudWriteSupport.Request(
            scope, RecurringMaintenanceWriteOperation.SetActive, id, request, idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            RecurringMaintenanceCrudWriteSupport.IdempotencyKey(command),
            RecurringMaintenanceCrudWriteSupport.Write(
                command, SetRecurringMaintenanceActiveAsync, AuthorizeReplayAsync), ct);
        return Response(outcome.Value);
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = RecurringMaintenanceCrudWriteSupport.Request(
            scope, RecurringMaintenanceWriteOperation.Delete, id, new object(), idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            RecurringMaintenanceCrudWriteSupport.IdempotencyKey(command),
            RecurringMaintenanceCrudWriteSupport.Write(
                command, DeleteRecurringMaintenanceAsync, AuthorizeReplayAsync), ct);
        return outcome.Value.Found;
    }

    private static RecurringMaintenanceTaskResponse? Response(
        RecurringMaintenanceWriteResult result) =>
        !result.Found || result.ResponseJson is null
            ? null
            : JsonSerializer.Deserialize<RecurringMaintenanceTaskResponse>(result.ResponseJson)
                ?? throw new AtomicReceiptInvariantException(
                    "The recurring maintenance receipt snapshot is invalid.");

    private async Task<RecurringMaintenanceWriteResult> CreateRecurringMaintenanceAsync(
        RecurringMaintenanceWriteRequest command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await BeginExecutionAsync(command, context, replay: false, ct);
        var request = RecurringMaintenanceCrudWriteSupport.Read<CreateRecurringMaintenanceTaskRequest>(command);
        if (!await ReferencesExistAsync(
                command.PortfolioId, request.PropertyId, request.UnitId, request.VendorId, ct))
        {
            return Missing(0);
        }

        var entity = new RecurringMaintenanceTask
        {
            PortfolioId = command.PortfolioId,
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
        _db.Add(entity);
        context.BindSemanticAudit(entity, Audit(command, 0, AuditLogOperation.Created,
            $"Recurring maintenance task {entity.Title} created"));
        await context.FlushBusinessAsync(ct);
        var responseJson = await SnapshotAsync(command.PortfolioId, entity.Id, ct);
        StageDataUpdate(context, command, entity.Id, now, responseJson);
        return Applied(entity.Id, responseJson);
    }

    private async Task<RecurringMaintenanceWriteResult> UpdateRecurringMaintenanceAsync(
        RecurringMaintenanceWriteRequest command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await BeginExecutionAsync(command, context, replay: false, ct);
        var entity = await FindTaskAsync(command, ct);
        if (entity is null) return Missing(command.EntityId);
        var request = RecurringMaintenanceCrudWriteSupport.Read<UpdateRecurringMaintenanceTaskRequest>(command);
        var effectiveUnitId = request.UnitId ?? entity.UnitId;
        var effectiveVendorId = request.VendorId ?? entity.VendorId;
        if (!await ReferencesExistAsync(
                command.PortfolioId, entity.PropertyId, effectiveUnitId, effectiveVendorId, ct))
        {
            return Missing(entity.Id);
        }

        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.VendorId.HasValue) entity.VendorId = request.VendorId;
        if (request.Title is not null) entity.Title = request.Title;
        if (request.Description is not null) entity.Description = request.Description;
        if (request.Category is not null) entity.Category = request.Category;
        if (request.RecurrenceInterval.HasValue)
            entity.RecurrenceInterval = request.RecurrenceInterval.Value;
        if (request.NextDueDate.HasValue) entity.NextDueDate = NormalizeDate(request.NextDueDate.Value);
        entity.ScheduledTime = request.ScheduledTime;
        entity.EstimatedCost = request.EstimatedCost;
        if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;
        if (request.Priority.HasValue) entity.Priority = request.Priority.Value;
        ClearAutomationFailureState(entity);
        entity.UpdatedAt = now;
        context.BindSemanticAudit(entity, Audit(command, entity.Id, AuditLogOperation.Updated,
            $"Recurring maintenance task {entity.Id} updated"));
        await context.FlushBusinessAsync(ct);
        var responseJson = await SnapshotAsync(command.PortfolioId, entity.Id, ct);
        StageDataUpdate(context, command, entity.Id, now, responseJson);
        return Applied(entity.Id, responseJson);
    }

    private async Task<RecurringMaintenanceWriteResult> SetRecurringMaintenanceActiveAsync(
        RecurringMaintenanceWriteRequest command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await BeginExecutionAsync(command, context, replay: false, ct);
        var entity = await FindTaskAsync(command, ct);
        if (entity is null) return Missing(command.EntityId);
        var request = RecurringMaintenanceCrudWriteSupport.Read<ToggleRecurringMaintenanceTaskActiveRequest>(command);
        entity.IsActive = request.IsActive;
        ClearAutomationFailureState(entity);
        entity.UpdatedAt = now;
        context.BindSemanticAudit(entity, Audit(command, entity.Id, AuditLogOperation.Updated,
            $"Recurring maintenance task {entity.Id} {(request.IsActive ? "activated" : "deactivated")}"));
        await context.FlushBusinessAsync(ct);
        var responseJson = await SnapshotAsync(command.PortfolioId, entity.Id, ct);
        StageDataUpdate(context, command, entity.Id, now, responseJson);
        return Applied(entity.Id, responseJson);
    }

    private async Task<RecurringMaintenanceWriteResult> DeleteRecurringMaintenanceAsync(
        RecurringMaintenanceWriteRequest command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await BeginExecutionAsync(command, context, replay: false, ct);
        var entity = await FindTaskAsync(command, ct);
        if (entity is null) return Missing(command.EntityId);
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        context.BindSemanticAudit(entity, Audit(command, entity.Id, AuditLogOperation.Deleted,
            $"Recurring maintenance task {entity.Id} deleted"));
        await context.FlushBusinessAsync(ct);
        StageDataUpdate(context, command, entity.Id, now, deleted: true);
        return Applied(entity.Id);
    }

    private Task<DateTime> BeginExecutionAsync(
        RecurringMaintenanceWriteRequest command,
        IAtomicCommandContext context,
        bool replay,
        CancellationToken ct) =>
        RecurringMaintenanceCrudWriteSupport.BeginExecutionAsync(
            command, context, (now, token) => AuthorizeAsync(command, now, replay, token), ct);

    private Task AuthorizeReplayAsync(
        RecurringMaintenanceWriteRequest command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        RecurringMaintenanceCrudWriteSupport.AuthorizeReplayAsync(
            command, context, (now, token) => AuthorizeAsync(command, now, replay: true, token), ct);

    private async Task AuthorizeAsync(
        RecurringMaintenanceWriteRequest command,
        DateTime now,
        bool replay,
        CancellationToken ct)
    {
        int? propertyId = command.Operation == RecurringMaintenanceWriteOperation.Create
            ? RecurringMaintenanceCrudWriteSupport.Read<CreateRecurringMaintenanceTaskRequest>(command).PropertyId
            : await _db.RecurringMaintenanceTasks.IgnoreQueryFilters()
                .Where(task => task.Id == command.EntityId && task.PortfolioId == command.PortfolioId)
                .Select(task => (int?)task.PropertyId)
                .SingleOrDefaultAsync(ct);
        var scope = new WorkspaceReadScope(
            command.PortfolioId, command.ActorUserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision);
        var authorized = propertyId.HasValue && await _db.Properties.AsNoTracking()
            .WhereAuthorizedForScope(_db, scope, CapabilityKeys.WorkManage, now)
            .AnyAsync(property => property.Id == propertyId.Value, ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(replay
                ? "Workspace access changed. Refresh and try again."
                : "The recurring maintenance task is outside the current Team role and property scope.");
        }
    }

    private Task<bool> ReferencesExistAsync(
        int portfolioId,
        int propertyId,
        int? unitId,
        int? vendorId,
        CancellationToken ct) =>
        _db.Properties.AsNoTracking()
            .Where(property => property.Id == propertyId && property.PortfolioId == portfolioId
                && property.DeletedAt == null)
            .AnyAsync(property =>
                (unitId == null || _db.Units.Any(unit => unit.Id == unitId
                    && unit.PortfolioId == portfolioId && unit.PropertyId == property.Id
                    && unit.DeletedAt == null))
                && (vendorId == null || _db.Vendors.Any(vendor => vendor.Id == vendorId
                    && vendor.PortfolioId == portfolioId && vendor.DeletedAt == null)), ct);

    private Task<RecurringMaintenanceTask?> FindTaskAsync(
        RecurringMaintenanceWriteRequest command,
        CancellationToken ct) =>
        _db.RecurringMaintenanceTasks.SingleOrDefaultAsync(task =>
            task.Id == command.EntityId && task.PortfolioId == command.PortfolioId
            && task.DeletedAt == null, ct);

    private async Task<string> SnapshotAsync(int portfolioId, int entityId, CancellationToken ct)
    {
        var response = await ProjectResponse(_db.RecurringMaintenanceTasks.AsNoTracking()
                .Where(task => task.Id == entityId && task.PortfolioId == portfolioId))
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private static void StageDataUpdate(
        IAtomicCommandContext context,
        RecurringMaintenanceWriteRequest command,
        int entityId,
        DateTime now,
        string? responseJson = null,
        bool deleted = false)
    {
        object data = responseJson is null ? new { } : JsonSerializer.Deserialize<JsonElement>(responseJson);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(RecurringMaintenanceTask), entityId,
                operation = deleted ? "delete" : "update", data,
            }),
            IdempotencyKey = $"recurring-maintenance:{command.PortfolioId}:{command.AccessContextId}:" +
                $"{command.Operation}:{entityId}:{command.DeliveryIdempotencyKey}:data-update",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    private static AtomicSemanticAudit Audit(
        RecurringMaintenanceWriteRequest command,
        int entityId,
        AuditLogOperation operation,
        string reason) =>
        new(command.PortfolioId, nameof(RecurringMaintenanceTask), entityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private static DateTime NormalizeDate(DateTime value) =>
        DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    private static void ClearAutomationFailureState(RecurringMaintenanceTask entity)
    {
        entity.WorkerClaimAttemptCount = 0;
        entity.WorkerClaimLastFailureReason = null;
        entity.WorkerClaimLastFailureAtUtc = null;
        entity.WorkerClaimQuarantinedAtUtc = null;
    }

    private IRequestWriteExecutor RequireWrites() =>
        _writes ?? throw new InvalidOperationException(
            "The shared request write executor is required for recurring maintenance changes.");

    private static RecurringMaintenanceWriteResult Missing(int entityId) =>
        new(false, false, entityId);

    private static RecurringMaintenanceWriteResult Applied(
        int entityId,
        string? responseJson = null) =>
        new(true, true, entityId, responseJson);

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
            AutomationFailureAttemptCount = t.WorkerClaimAttemptCount,
            AutomationFailureReason = t.WorkerClaimLastFailureReason,
            AutomationFailureAtUtc = t.WorkerClaimLastFailureAtUtc,
            AutomationQuarantinedAtUtc = t.WorkerClaimQuarantinedAtUtc,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
        });
}
