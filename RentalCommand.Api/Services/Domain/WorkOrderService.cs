using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
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
using RentalCommand.Data.Operations;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IWorkOrderService"/>
public class WorkOrderService : IWorkOrderService
{
    private const string EntityType = "WorkOrder";

    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestWriteExecutor? _writes;

    public WorkOrderService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IMessagePublisher publisher,
        IFileStorage files,
        ILogger<WorkOrderService> logger,
        TimeProvider timeProvider,
        IAtomicUnitOfWork? atomic = null,
        IRequestWriteExecutor? writes = null)
    {
        _db = db;
        _files = files;
        _timeProvider = timeProvider;
        _writes = writes;
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
            ApplyMaintenanceVisibility(response);
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
                _timeProvider.UtcNow());

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

        var hasManagementAccess = managementProperties is null || await managementProperties.AnyAsync(property =>
            property.Id == entity.PropertyId && property.PortfolioId == entity.PortfolioId, ct);
        if (hasManagementAccess && managementProperties is not null)
        {
            entity = await _db.WorkOrders
                .AsNoTracking()
                .Include(w => w.Property)
                .Include(w => w.Unit)
                .Include(w => w.Vendor)
                .Include(w => w.Tenant)
                .SingleAsync(
                    item => item.Id == id && item.PortfolioId == portfolioId,
                    ct);
        }

        var recentEvents = _db.WorkOrderStatusEvents
            .AsNoTracking()
            .Where(e => e.WorkOrderId == id && e.PortfolioId == portfolioId)
            .Where(e => hasManagementAccess || e.Visibility == "Public")
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(50);

        var events = await recentEvents
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var response = WorkOrderDetailResponse.FromEntity(entity, events);
        if (!hasManagementAccess)
        {
            var safeContext = await _db.Database
                .SqlQuery<AssignedWorkOrderDetailContextRow>($"""
                    SELECT assigned_context."WorkOrderId",
                           assigned_context."PropertyName",
                           assigned_context."UnitNumber",
                           assigned_context."TenantName"
                    FROM public.rc_api_assigned_work_order_detail_context(
                        {portfolioId}, {id}) AS assigned_context
                    """)
                .SingleOrDefaultAsync(ct);
            if (safeContext is null)
            {
                return null;
            }

            response.PropertyName = safeContext.PropertyName;
            response.UnitNumber = safeContext.UnitNumber;
            response.TenantName = safeContext.TenantName;
        }
        response.DetailRole = "manager";
        response.Capabilities = WorkOrderDetailCapabilities.Manager(response.Status);
        response.ResidentNames = string.IsNullOrWhiteSpace(response.TenantName)
            ? []
            : [response.TenantName];
        response.Activity = events.Select(e => new WorkOrderActivityResponse
        {
            Id = e.Id,
            Kind = e.Kind,
            FromStatus = e.FromStatus,
            ToStatus = e.ToStatus,
            Note = e.Note,
            ActorLabel = e.ChangedByLabel ?? "System",
            Visibility = e.Visibility,
            CreatedAtUtc = e.CreatedAtUtc,
        }).ToList();
        if (!hasManagementAccess)
        {
            response.DetailRole = "maintenance";
            response.Capabilities = WorkOrderDetailCapabilities.Maintenance(response.Status);
            ApplyMaintenanceVisibility(response);
        }

        // Whether an OPEN vendor dispatch (texted, still awaiting the vendor's DONE) exists for this work
        // order — a single EXISTS computed DB-side, never loaded-then-counted. Drives the detail page's
        // "vendor has the job … will close on DONE" banner so it reflects a real dispatch rather than a
        // mere vendor assignment. "Open" = Dispatched/Acknowledged, matching VendorDispatchService.
        var activeDispatch = await _db.VendorDispatches
            .AsNoTracking()
            .Where(d => d.WorkOrderId == id
                && d.PortfolioId == portfolioId
                && (d.Status == VendorDispatchStatus.Dispatched
                    || d.Status == VendorDispatchStatus.Acknowledged))
            .OrderByDescending(d => d.DispatchedAtUtc)
            .ThenByDescending(d => d.Id)
            .Select(d => new
            {
                DispatchId = d.Id,
                d.VendorId,
                VendorName = d.Vendor == null ? null : d.Vendor.Name,
            })
            .FirstOrDefaultAsync(ct);
        response.HasActiveDispatch = activeDispatch is not null;
        response.ActiveDispatchId = activeDispatch?.DispatchId;
        response.ActiveDispatchVendorId = activeDispatch?.VendorId;
        response.ActiveDispatchVendorName = activeDispatch?.VendorName;

        var scan = await _db.FindLatestAvailableEntityFileAsync(_files, portfolioId, EntityType, id, ct);
        if (scan is not null)
        {
            response.HasScan = true;
            response.ScanIsImage = scan.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        return response;
    }

    private sealed class AssignedWorkOrderDetailContextRow
    {
        public int WorkOrderId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public string? UnitNumber { get; init; }
        public string? TenantName { get; init; }
    }

    private static void ApplyMaintenanceVisibility(WorkOrderResponse response)
    {
        response.PortfolioId = null;
        response.PropertyId = null;
        response.UnitId = null;
        response.TenantId = null;
        response.LeaseManagementId = null;
        response.VendorId = null;
        response.RecurringMaintenanceTaskId = null;
        response.EstimatedCost = null;
        response.ActualCost = null;
        response.CreatedBy = null;
        response.VendorName = null;
        if (response is WorkOrderDetailResponse detail)
        {
            detail.PrivateManagementNotes = null;
        }
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
            request.TechnicianAccessInstructions,
            request.SubmittedByLabel, request.RequesterName, request.RequesterPhone, request.RequesterEmail,
            request.ResidentMustBePresent, request.CallBeforeEntry, request.CallIfNotHome,
            request.PermissionToEnter, request.EntryNotes, request.PetWarnings, request.AccessWarnings,
            request.Category, request.Priority, request.Status,
            request.RequestedAt?.ToUtc(),
            request.ScheduledFor, request.ScheduledWindowEnd,
            request.ScheduledFor.ToUtcDateTime(), request.ScheduledWindowEnd.ToUtcDateTime(),
            request.CompletedAt.ToUtc(), request.EstimatedCost, request.ActualCost,
            request.CreatedBy, request.ExtractedData, _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            WorkOrderCrudWriteSupport.IdempotencyKey(idempotencyKey),
            WorkOrderCrudWriteSupport.Write(command, CreateWorkOrderAsync, AuthorizeReplayAsync), ct);
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
            request.TechnicianAccessInstructions,
            request.SubmittedByLabel, request.RequesterName, request.RequesterPhone, request.RequesterEmail,
            request.ResidentMustBePresent, request.CallBeforeEntry, request.CallIfNotHome,
            request.PermissionToEnter, request.EntryNotes, request.PetWarnings, request.AccessWarnings,
            request.Category, request.Priority, request.Status,
            request.StatusNote,
            request.RequestedAt?.ToUtc(), request.ScheduledFor.ToUtcDateTime(),
            request.ScheduledWindowEnd.ToUtcDateTime(), request.CompletedAt.ToUtc(),
            request.EstimatedCost, request.ActualCost,
            _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            WorkOrderCrudWriteSupport.IdempotencyKey(idempotencyKey),
            WorkOrderCrudWriteSupport.Write(command, UpdateWorkOrderAsync, AuthorizeReplayAsync), ct);
        return Response(outcome.Value);
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new DeleteWorkOrderCommand(
            scope.PortfolioId, Actor(scope), id, _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            WorkOrderCrudWriteSupport.IdempotencyKey(idempotencyKey),
            WorkOrderCrudWriteSupport.Write(command, DeleteWorkOrderAsync, AuthorizeReplayAsync), ct);
        return outcome.Value.Outcome == OperationMutationOutcome.Applied;
    }

    public async Task<WorkOrderMutationReceipt?> CommentAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        WorkOrderCommentRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new AddStaffWorkOrderCommentCommand(
            scope.PortfolioId, Actor(scope), id, request.Body, request.IsPrivate,
            _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            WorkOrderCrudWriteSupport.IdempotencyKey(idempotencyKey),
            WorkOrderCrudWriteSupport.Write(command, AddCommentAsync, AuthorizeReplayAsync), ct);
        return Receipt(outcome.Value);
    }

    private Task<WorkOrderMutationResult> CreateWorkOrderAsync(
        CreateWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        new CreateWorkOrderRule(_db).HandleAsync(command, context, ct);

    private Task<WorkOrderMutationResult> UpdateWorkOrderAsync(
        UpdateWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        new UpdateWorkOrderRule(_db).HandleAsync(command, context, ct);

    private Task<WorkOrderMutationResult> DeleteWorkOrderAsync(
        DeleteWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        new DeleteWorkOrderRule(_db).HandleAsync(command, context, ct);

    private Task<WorkOrderMutationResult> AddCommentAsync(
        AddStaffWorkOrderCommentCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        new AddStaffWorkOrderCommentRule(_db).HandleAsync(command, context, ct);

    private Task AuthorizeReplayAsync(
        CreateWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        new CreateWorkOrderRule(_db).AuthorizeReplayAsync(command, context, ct);

    private Task AuthorizeReplayAsync(
        UpdateWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        new UpdateWorkOrderRule(_db).AuthorizeReplayAsync(command, context, ct);

    private Task AuthorizeReplayAsync(
        DeleteWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        new DeleteWorkOrderRule(_db).AuthorizeReplayAsync(command, context, ct);

    private Task AuthorizeReplayAsync(
        AddStaffWorkOrderCommentCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        new AddStaffWorkOrderCommentRule(_db).AuthorizeReplayAsync(command, context, ct);

    private IRequestWriteExecutor RequireWrites() => _writes ?? throw new InvalidOperationException(
        "The shared request write executor is required for work-order changes.");

    private static StaffOperationActor Actor(WorkspaceReadScope scope) => new(
        scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private static WorkOrderResponse? Response(WorkOrderMutationResult result) =>
        result.Outcome == OperationMutationOutcome.NotFound || result.Snapshot is null
            ? null
            : ToResponse(result.Snapshot);

    private static WorkOrderMutationReceipt? Receipt(WorkOrderMutationResult result) =>
        result.Outcome == OperationMutationOutcome.NotFound || result.Receipt is null
            ? null
            : ToReceipt(result.Receipt);

    private static WorkOrderResponse ToResponse(WorkOrderMutationSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        PortfolioId = snapshot.PortfolioId,
        PropertyId = snapshot.PropertyId,
        UnitId = snapshot.UnitId,
        TenantId = snapshot.TenantId,
        LeaseManagementId = snapshot.LeaseManagementId,
        VendorId = snapshot.VendorId,
        RecurringMaintenanceTaskId = snapshot.RecurringMaintenanceTaskId,
        Title = snapshot.Title,
        Description = snapshot.Description,
        TechnicianAccessInstructions = snapshot.TechnicianAccessInstructions,
        SubmittedByLabel = snapshot.SubmittedByLabel,
        RequesterName = snapshot.RequesterName,
        RequesterPhone = snapshot.RequesterPhone,
        RequesterEmail = snapshot.RequesterEmail,
        ResidentMustBePresent = snapshot.ResidentMustBePresent,
        CallBeforeEntry = snapshot.CallBeforeEntry,
        CallIfNotHome = snapshot.CallIfNotHome,
        PermissionToEnter = snapshot.PermissionToEnter,
        EntryNotes = snapshot.EntryNotes,
        PetWarnings = snapshot.PetWarnings,
        AccessWarnings = snapshot.AccessWarnings,
        Category = snapshot.Category,
        Priority = snapshot.Priority,
        Status = snapshot.Status,
        RequestedAt = snapshot.RequestedAt,
        ScheduledFor = snapshot.ScheduledFor,
        ScheduledWindowEnd = snapshot.ScheduledWindowEnd,
        CompletedAt = snapshot.CompletedAt,
        EstimatedCost = snapshot.EstimatedCost,
        ActualCost = snapshot.ActualCost,
        CreatedBy = snapshot.CreatedBy,
        UpdatedAt = snapshot.UpdatedAt,
        PropertyName = snapshot.PropertyName,
        UnitNumber = snapshot.UnitNumber,
        VendorName = snapshot.VendorName,
        TenantName = snapshot.TenantName,
    };

    private static WorkOrderMutationReceipt ToReceipt(WorkOrderMutationActivityReceipt receipt) => new()
    {
        EntityId = receipt.EntityId,
        Outcome = receipt.Outcome.ToString(),
        ActivityId = receipt.ActivityId,
        CommittedAtUtc = receipt.CommittedAtUtc,
    };

}
