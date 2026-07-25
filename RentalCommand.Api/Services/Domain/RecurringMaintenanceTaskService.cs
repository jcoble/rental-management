using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
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
    private static readonly AtomicJsonResultCodec<AtomicRecurringMaintenanceMutationResult> MutationCodec =
        new("recurring-maintenance.mutation.v1");

    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;

    public RecurringMaintenanceTaskService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _timeProvider = timeProvider;
        _atomic = atomic;
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
        var command = Command(scope, AtomicRecurringMaintenanceOperation.Create, 0, request, idempotencyKey);
        var outcome = await _atomic.ExecuteAsync(Identity(command), command, MutationCodec, ct);
        return Response(outcome.Value);
    }

    public async Task<RecurringMaintenanceTaskResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateRecurringMaintenanceTaskRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = Command(scope, AtomicRecurringMaintenanceOperation.Update, id, request, idempotencyKey);
        var outcome = await _atomic.ExecuteAsync(Identity(command), command, MutationCodec, ct);
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
        var command = Command(scope, AtomicRecurringMaintenanceOperation.SetActive, id, request, idempotencyKey);
        var outcome = await _atomic.ExecuteAsync(Identity(command), command, MutationCodec, ct);
        return Response(outcome.Value);
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = Command(scope, AtomicRecurringMaintenanceOperation.Delete, id, new object(), idempotencyKey);
        var outcome = await _atomic.ExecuteAsync(Identity(command), command, MutationCodec, ct);
        return outcome.Value.Found;
    }

    private static AtomicRecurringMaintenanceMutationCommand Command(
        WorkspaceReadScope scope,
        AtomicRecurringMaintenanceOperation operation,
        int entityId,
        object request,
        string idempotencyKey) => new(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            operation,
            entityId,
            JsonSerializer.Serialize(request),
            idempotencyKey);

    private static AtomicCommandIdentity Identity(AtomicRecurringMaintenanceMutationCommand command)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            command.DeliveryIdempotencyKey)));
        return new AtomicCommandIdentity(
            $"recurring-maintenance.{command.Operation.ToString().ToLowerInvariant()}", digest);
    }

    private static RecurringMaintenanceTaskResponse? Response(
        AtomicRecurringMaintenanceMutationResult result) =>
        !result.Found || result.ResponseJson is null
            ? null
            : JsonSerializer.Deserialize<RecurringMaintenanceTaskResponse>(result.ResponseJson)
                ?? throw new AtomicReceiptInvariantException(
                    "The recurring maintenance receipt snapshot is invalid.");

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
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
        });
}
