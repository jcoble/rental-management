using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IWorkOrderService"/>
public class WorkOrderService : IWorkOrderService
{
    private static readonly AtomicJsonResultCodec<OperationMutationResult> MutationCodec =
        new("work-order.mutation.v1");
    private const string EntityType = "WorkOrder";

    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork? _atomic;

    public WorkOrderService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IMessagePublisher publisher,
        IFileStorage files,
        ILogger<WorkOrderService> logger,
        TimeProvider timeProvider,
        IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _files = files;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    public async Task<IReadOnlyList<WorkOrderResponse>> ListAsync(int portfolioId, int? propertyId, int? unitId, int? vendorId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, ToWorkOrderListQuery(query, propertyId, unitId, vendorId), ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<WorkOrderResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope,
        int? propertyId,
        int? unitId,
        int? vendorId,
        ListQuery query,
        CancellationToken ct = default)
    {
        var page = await ListPageAuthorizedAsync(
            scope,
            ToWorkOrderListQuery(query, propertyId, unitId, vendorId),
            ct);
        return page.Items;
    }

    public async Task<WorkOrderListResponse> ListPageAsync(int portfolioId, WorkOrderListQuery query, CancellationToken ct = default)
    {
        var q = _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId);

        return await ListPageFromQueryAsync(q, query, ct);
    }

    public Task<WorkOrderListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope,
        WorkOrderListQuery query,
        CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();
        var q = _db.WorkOrders
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                new[] { CapabilityKeys.WorkRead, CapabilityKeys.AssignedWorkRead },
                now);
        var managementProperties = _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, [CapabilityKeys.WorkRead], now);

        return ListPageFromQueryAsync(q, query, ct, managementProperties);
    }

    private static async Task<WorkOrderListResponse> ListPageFromQueryAsync(
        IQueryable<WorkOrder> q,
        WorkOrderListQuery query,
        CancellationToken ct,
        IQueryable<Property>? managementProperties = null)
    {

        if (query.PropertyId.HasValue)
        {
            q = q.Where(w => w.PropertyId == query.PropertyId.Value);
        }

        if (query.UnitId.HasValue)
        {
            q = q.Where(w => w.UnitId == query.UnitId.Value);
        }

        if (query.VendorId.HasValue)
        {
            q = q.Where(w => w.VendorId == query.VendorId.Value);
        }

        if (query.OpenOnly)
        {
            q = q.Where(w =>
                w.Status != WorkOrderStatus.Completed &&
                w.Status != WorkOrderStatus.Cancelled &&
                w.Status != WorkOrderStatus.Archived);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(w => w.Status == query.Status.Value);
        }

        if (query.Priority.HasValue)
        {
            q = q.Where(w => w.Priority == query.Priority.Value);
        }

        if (query.RequestedFrom.HasValue)
        {
            var requestedFrom = query.RequestedFrom.Value.ToUtc();
            q = q.Where(w => w.RequestedAt >= requestedFrom);
        }

        if (query.RequestedTo.HasValue)
        {
            var requestedToExclusive = ToExclusiveUpperBound(query.RequestedTo.Value);
            q = q.Where(w => w.RequestedAt < requestedToExclusive);
        }

        if (query.ScheduledFrom.HasValue)
        {
            var scheduledFrom = query.ScheduledFrom.Value.ToUtc();
            q = q.Where(w => w.ScheduledFor != null && w.ScheduledFor >= scheduledFrom);
        }

        if (query.ScheduledTo.HasValue)
        {
            var scheduledToExclusive = ToExclusiveUpperBound(query.ScheduledTo.Value);
            q = q.Where(w => w.ScheduledFor != null && w.ScheduledFor < scheduledToExclusive);
        }

        if (query.CompletedFrom.HasValue)
        {
            var completedFrom = query.CompletedFrom.Value.ToUtc();
            q = q.Where(w => w.CompletedAt != null && w.CompletedAt >= completedFrom);
        }

        if (query.CompletedTo.HasValue)
        {
            var completedToExclusive = ToExclusiveUpperBound(query.CompletedTo.Value);
            q = q.Where(w => w.CompletedAt != null && w.CompletedAt < completedToExclusive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(w =>
                EF.Functions.ILike(w.Title, $"%{term}%") ||
                EF.Functions.ILike(w.Description, $"%{term}%") ||
                EF.Functions.ILike(w.Category, $"%{term}%") ||
                (w.Property != null && EF.Functions.ILike(w.Property.Name, $"%{term}%")) ||
                (w.Unit != null && EF.Functions.ILike(w.Unit.UnitNumber, $"%{term}%")) ||
                (w.Vendor != null && EF.Functions.ILike(w.Vendor.Name, $"%{term}%")) ||
                (w.Tenant != null && (
                    EF.Functions.ILike(w.Tenant.FirstName, $"%{term}%") ||
                    EF.Functions.ILike(w.Tenant.LastName, $"%{term}%"))));
        }

        var totalCount = await q.CountAsync(ct);

        q = query.SortField switch
        {
            "title" => query.SortDescending ? q.OrderByDescending(w => w.Title) : q.OrderBy(w => w.Title),
            "propertyname" => query.SortDescending ? q.OrderByDescending(w => w.Property!.Name) : q.OrderBy(w => w.Property!.Name),
            "unitnumber" => query.SortDescending ? q.OrderByDescending(w => w.Unit!.UnitNumber) : q.OrderBy(w => w.Unit!.UnitNumber),
            "vendorname" => query.SortDescending ? q.OrderByDescending(w => w.Vendor!.Name) : q.OrderBy(w => w.Vendor!.Name),
            "tenantname" => query.SortDescending
                ? q.OrderByDescending(w => w.Tenant!.FirstName).ThenByDescending(w => w.Tenant!.LastName)
                : q.OrderBy(w => w.Tenant!.FirstName).ThenBy(w => w.Tenant!.LastName),
            "status" => query.SortDescending ? q.OrderByDescending(w => w.Status) : q.OrderBy(w => w.Status),
            "priority" => query.SortDescending ? q.OrderByDescending(w => w.Priority) : q.OrderBy(w => w.Priority),
            "fieldqueue" => q.OrderByDescending(w => w.Priority).ThenBy(w => w.RequestedAt),
            "requestedat" => query.SortDescending ? q.OrderByDescending(w => w.RequestedAt) : q.OrderBy(w => w.RequestedAt),
            "scheduledfor" => query.SortDescending ? q.OrderByDescending(w => w.ScheduledFor) : q.OrderBy(w => w.ScheduledFor),
            "completedat" => query.SortDescending ? q.OrderByDescending(w => w.CompletedAt) : q.OrderBy(w => w.CompletedAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(w => w.UpdatedAt) : q.OrderBy(w => w.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(w => w.RequestedAt) : q.OrderBy(w => w.RequestedAt),
        };

        // Project the display names (property / unit / vendor / tenant) in the same SELECT via LEFT
        // JOINs — EF translates the optional-navigation member access to a join, so there is no
        // per-row follow-up query. The full related entities are never materialized; only the name
        // columns ride along.
        var rowQuery = managementProperties is null
            ? q.Select(w => new WorkOrderListRow(
                w,
                w.Property != null ? w.Property.Name : null,
                w.Unit != null ? w.Unit.UnitNumber : null,
                w.Vendor != null ? w.Vendor.Name : null,
                w.Tenant != null ? ((w.Tenant.FirstName + " " + w.Tenant.LastName)).Trim() : null,
                true))
            : q.Select(w => new WorkOrderListRow(
                w,
                w.Property != null ? w.Property.Name : null,
                w.Unit != null ? w.Unit.UnitNumber : null,
                w.Vendor != null ? w.Vendor.Name : null,
                w.Tenant != null ? ((w.Tenant.FirstName + " " + w.Tenant.LastName)).Trim() : null,
                managementProperties.Any(property =>
                    property.Id == w.PropertyId && property.PortfolioId == w.PortfolioId)));
        var rows = await rowQuery
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new WorkOrderListResponse
        {
            Items = rows.Select(ToListResponse).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static WorkOrderListQuery ToWorkOrderListQuery(ListQuery query, int? propertyId, int? unitId, int? vendorId)
    {
        var workOrderQuery = query as WorkOrderListQuery;
        return new WorkOrderListQuery
        {
            Skip = query.Skip,
            Take = query.Take,
            Search = query.Search,
            Sort = query.Sort,
            PropertyId = propertyId ?? workOrderQuery?.PropertyId,
            UnitId = unitId ?? workOrderQuery?.UnitId,
            VendorId = vendorId ?? workOrderQuery?.VendorId,
            OpenOnly = workOrderQuery?.OpenOnly ?? false,
            Status = workOrderQuery?.Status,
            Priority = workOrderQuery?.Priority,
            RequestedFrom = workOrderQuery?.RequestedFrom,
            RequestedTo = workOrderQuery?.RequestedTo,
            ScheduledFrom = workOrderQuery?.ScheduledFrom,
            ScheduledTo = workOrderQuery?.ScheduledTo,
            CompletedFrom = workOrderQuery?.CompletedFrom,
            CompletedTo = workOrderQuery?.CompletedTo,
        };
    }

    private static DateTime ToExclusiveUpperBound(DateTime value)
    {
        var utc = value.ToUtc();
        return value.TimeOfDay == TimeSpan.Zero ? utc.AddDays(1) : utc;
    }

    private sealed record WorkOrderListRow(
        WorkOrder WorkOrder, string? PropertyName, string? UnitNumber, string? VendorName,
        string? TenantName, bool CanReadFinancialDetails);

    private static WorkOrderResponse ToListResponse(WorkOrderListRow row)
    {
        var response = WorkOrderResponse.FromEntity(row.WorkOrder);
        response.PropertyName = row.PropertyName;
        response.UnitNumber = row.UnitNumber;
        response.VendorName = row.VendorName;
        response.TenantName = row.TenantName;
        if (!row.CanReadFinancialDetails)
        {
            response.EstimatedCost = null;
            response.ActualCost = null;
            response.LeaseManagementId = null;
        }
        return response;
    }

    public async Task<WorkOrderDetailResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var q = _db.WorkOrders
            .AsNoTracking()
            .Include(w => w.Property)
            .Include(w => w.Unit)
            .Include(w => w.Vendor)
            .Include(w => w.Tenant)
            .Where(w => w.Id == id && w.PortfolioId == portfolioId);

        return await GetFromQueryAsync(q, portfolioId, id, ct);
    }

    public Task<WorkOrderDetailResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var q = _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.Id == id)
            .WhereAuthorized(
                _db,
                scope,
                new[] { CapabilityKeys.WorkRead, CapabilityKeys.AssignedWorkRead },
                _timeProvider.UtcNow())
            .Include(w => w.Property)
            .Include(w => w.Unit)
            .Include(w => w.Vendor)
            .Include(w => w.Tenant);

        var managementProperties = _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, [CapabilityKeys.WorkRead], _timeProvider.UtcNow());
        return GetFromQueryAsync(q, scope.PortfolioId, id, ct, managementProperties);
    }

    private async Task<WorkOrderDetailResponse?> GetFromQueryAsync(
        IQueryable<WorkOrder> q,
        int portfolioId,
        int id,
        CancellationToken ct,
        IQueryable<Property>? managementProperties = null)
    {
        var entity = await q.FirstOrDefaultAsync(ct);
        if (entity == null)
        {
            return null;
        }

        var events = await _db.WorkOrderStatusEvents
            .AsNoTracking()
            .Where(e => e.WorkOrderId == id && e.PortfolioId == portfolioId)
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var response = WorkOrderDetailResponse.FromEntity(entity, events);
        if (managementProperties is not null && !await managementProperties.AnyAsync(property =>
                property.Id == entity.PropertyId && property.PortfolioId == entity.PortfolioId, ct))
        {
            response.EstimatedCost = null;
            response.ActualCost = null;
            response.LeaseManagementId = null;
        }

        // Whether an OPEN vendor dispatch (texted, still awaiting the vendor's DONE) exists for this work
        // order — a single EXISTS computed DB-side, never loaded-then-counted. Drives the detail page's
        // "vendor has the job … will close on DONE" banner so it reflects a real dispatch rather than a
        // mere vendor assignment. "Open" = Dispatched/Acknowledged, matching VendorDispatchService.
        response.HasActiveDispatch = await _db.VendorDispatches
            .AnyAsync(d => d.WorkOrderId == id
                && d.PortfolioId == portfolioId
                && (d.Status == VendorDispatchStatus.Dispatched
                    || d.Status == VendorDispatchStatus.Acknowledged), ct);

        var scan = await _db.FindLatestAvailableEntityFileAsync(_files, portfolioId, EntityType, id, ct);
        if (scan is not null)
        {
            response.HasScan = true;
            response.ScanIsImage = scan.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        return response;
    }

    public async Task<WorkOrderResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateWorkOrderRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var actor = Actor(scope);
        var command = new CreateWorkOrderCommand(
            scope.PortfolioId, actor, request.PropertyId, request.UnitId, request.TenantId,
            request.LeaseManagementId, request.VendorId, request.Title, request.Description,
            request.TechnicianAccessInstructions, request.Category, request.Priority, request.Status,
            request.RequestedAt?.ToUtc(),
            request.ScheduledFor, request.ScheduledWindowEnd,
            request.ScheduledFor.ToUtcDateTime(), request.ScheduledWindowEnd.ToUtcDateTime(),
            request.CompletedAt.ToUtc(), request.EstimatedCost, request.ActualCost,
            request.CreatedBy, request.ExtractedData, idempotencyKey);
        var outcome = await Atomic.ExecuteAsync(
            Identity("work-order.create", idempotencyKey), command, MutationCodec, ct);
        return Response(outcome.Value);
    }

    public async Task<WorkOrderResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateWorkOrderRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new UpdateWorkOrderCommand(
            scope.PortfolioId, Actor(scope), id, request.UnitId, request.ClearUnit,
            request.TenantId, request.ClearTenant, request.LeaseManagementId,
            request.ClearLeaseManagement, request.VendorId, request.Title, request.Description,
            request.TechnicianAccessInstructions, request.Category, request.Priority, request.Status,
            request.StatusNote,
            request.RequestedAt?.ToUtc(), request.ScheduledFor.ToUtcDateTime(),
            request.ScheduledWindowEnd.ToUtcDateTime(), request.CompletedAt.ToUtc(),
            request.EstimatedCost, request.ActualCost,
            _timeProvider.GetUtcNow().UtcDateTime, idempotencyKey);
        var outcome = await Atomic.ExecuteAsync(
            Identity("work-order.update", idempotencyKey), command, MutationCodec, ct);
        return Response(outcome.Value);
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new DeleteWorkOrderCommand(
            scope.PortfolioId, Actor(scope), id, idempotencyKey);
        var outcome = await Atomic.ExecuteAsync(
            Identity("work-order.delete", idempotencyKey), command, MutationCodec, ct);
        return outcome.Value.Outcome == OperationMutationOutcome.Applied;
    }

    private IAtomicUnitOfWork Atomic => _atomic ?? throw new InvalidOperationException(
        "Atomic work-order mutations are not configured.");

    private static StaffOperationActor Actor(WorkspaceReadScope scope) => new(
        scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private static AtomicCommandIdentity Identity(string operation, string key)
    {
        var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return new AtomicCommandIdentity(operation, digest);
    }

    private static WorkOrderResponse? Response(OperationMutationResult result) =>
        result.Outcome == OperationMutationOutcome.NotFound || result.ResponseJson is null
            ? null
            : JsonSerializer.Deserialize<WorkOrderResponse>(result.ResponseJson);

}
