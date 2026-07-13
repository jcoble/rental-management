using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IVendorDispatchService"/>
public class VendorDispatchService : IVendorDispatchService
{
    private const string VendorEntityType = "Vendor";
    private const string WorkOrderEntityType = "WorkOrder";
    private const string DispatchEntityType = "VendorDispatch";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly ILogger<VendorDispatchService> _logger;
    private readonly TimeProvider _timeProvider;

    public VendorDispatchService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IAtomicUnitOfWork atomic,
        ILogger<VendorDispatchService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _atomic = atomic;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public Task<DispatchResult> DispatchAsync(
        int portfolioId,
        int workOrderId,
        DispatchWorkOrderRequest request,
        int? changedByUserId,
        CancellationToken ct = default) =>
        DispatchCoreAsync(portfolioId, workOrderId, request, changedByUserId, authorizationScope: null, ct);

    public Task<DispatchResult> DispatchAuthorizedAsync(
        WorkspaceReadScope scope,
        int workOrderId,
        DispatchWorkOrderRequest request,
        int? changedByUserId,
        CancellationToken ct = default) =>
        DispatchCoreAsync(scope.PortfolioId, workOrderId, request, changedByUserId, scope, ct);

    private async Task<DispatchResult> DispatchCoreAsync(
        int portfolioId,
        int workOrderId,
        DispatchWorkOrderRequest request,
        int? changedByUserId,
        WorkspaceReadScope? authorizationScope,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        var workOrders = _db.WorkOrders
            .AsNoTracking()
            .Where(workOrder => workOrder.Id == workOrderId && workOrder.PortfolioId == portfolioId);
        if (authorizationScope is { } scope)
        {
            workOrders = workOrders.WhereAuthorized(
                _db, scope, [CapabilityKeys.WorkManage], _timeProvider.UtcNow());
        }

        var workOrder = await workOrders.FirstOrDefaultAsync(ct);
        if (workOrder is null)
        {
            return DispatchResult.NotFound();
        }

        var vendor = await _db.Vendors
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == request.VendorId && v.PortfolioId == portfolioId, ct);
        if (vendor is null)
        {
            return DispatchResult.NotFound();
        }

        var vendorPhone = SmsPhone.Normalize(vendor.Phone);
        if (string.IsNullOrWhiteSpace(vendorPhone))
        {
            return DispatchResult.NoPhone();
        }

        // Property/unit context for the job summary (best-effort labels).
        var property = await _db.Properties
            .AsNoTracking()
            .Where(p => p.Id == workOrder.PropertyId)
            .Select(p => new { p.Name, p.AddressLine1, p.City, p.State })
            .FirstOrDefaultAsync(ct);

        string? unitNumber = null;
        if (workOrder.UnitId.HasValue)
        {
            unitNumber = await _db.Units
                .AsNoTracking()
                .Where(u => u.Id == workOrder.UnitId.Value)
                .Select(u => u.UnitNumber)
                .FirstOrDefaultAsync(ct);
        }

        var now = _timeProvider.UtcNow();
        var message = BuildJobSms(workOrder, property?.Name, property?.AddressLine1, unitNumber, request.Note);

        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "vendor-dispatch.create",
                $"{portfolioId}:{workOrderId}:{request.IdempotencyKey.Trim()}"),
            new DispatchWorkOrderToVendorCommand(
                portfolioId,
                workOrderId,
                vendor.Id,
                vendorPhone,
                message,
                changedByUserId,
                now,
                authorizationScope is { } access
                    ? new DispatchManagementAccess(
                        access.SessionId,
                        access.UserId,
                        access.AccessContextId,
                        access.AccessRevision)
                    : null),
            new AtomicJsonResultCodec<DispatchWorkOrderToVendorResult>("vendor-dispatch.create.v1"),
            ct);
        if (outcome.Value.Outcome == DispatchWorkOrderToVendorOutcome.NotFound)
        {
            return DispatchResult.NotFound();
        }
        if (outcome.Value.Outcome == DispatchWorkOrderToVendorOutcome.AlreadyDispatched)
        {
            return DispatchResult.AlreadyDispatched();
        }

        workOrder.VendorId = outcome.Value.VendorId;
        workOrder.UpdatedAt = outcome.Value.DispatchedAtUtc;
        var response = new VendorDispatchResponse
        {
            Id = outcome.Value.DispatchId,
            PortfolioId = outcome.Value.PortfolioId,
            WorkOrderId = outcome.Value.WorkOrderId,
            VendorId = outcome.Value.VendorId,
            Status = outcome.Value.Status,
            DispatchedAtUtc = outcome.Value.DispatchedAtUtc,
            Message = outcome.Value.Message,
        };
        await SafeAsync("dispatch broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId, DispatchEntityType, response.Id, response, ct));
        await SafeAsync("work order broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId, WorkOrderEntityType, workOrder.Id, WorkOrderResponse.FromEntity(workOrder), ct));

        return DispatchResult.Ok(response);
    }

    public Task<VendorRatingResponse?> RateAsync(
        WorkspaceReadScope scope,
        int vendorId,
        CreateVendorRatingRequest request,
        CancellationToken ct = default) =>
        _db.ExecuteAuthorizedMutationAsync(async token =>
    {
        var portfolioId = scope.PortfolioId;
        var allProperties = _db.AuthorizedWorkspaceAssignments(
            scope,
            [CapabilityKeys.WorkManage],
            CapabilityAuthorizationTargetKind.Property,
            _timeProvider.UtcNow());
        var authorizedWorkOrders = _db.WorkOrders
            .AsNoTracking()
            .WhereAuthorized(_db, scope, [CapabilityKeys.WorkManage], _timeProvider.UtcNow());
        var vendor = await _db.Vendors
            .Where(v => v.Id == vendorId && v.PortfolioId == portfolioId &&
                        (request.WorkOrderId.HasValue
                            ? authorizedWorkOrders.Any(workOrder => workOrder.Id == request.WorkOrderId.Value)
                            : allProperties.Any()))
            .FirstOrDefaultAsync(token);
        if (vendor is null)
        {
            return null;
        }

        // Keep an out-of-scope work order from being linked into this portfolio's rating.
        int? workOrderId = null;
        if (request.WorkOrderId.HasValue)
        {
            var inScope = await authorizedWorkOrders
                .AnyAsync(w => w.Id == request.WorkOrderId.Value, token);
            if (inScope)
            {
                workOrderId = request.WorkOrderId.Value;
            }
            else
            {
                return null;
            }
        }

        var now = _timeProvider.UtcNow();
        var rating = new VendorRating
        {
            PortfolioId = portfolioId,
            VendorId = vendorId,
            WorkOrderId = workOrderId,
            Stars = Math.Clamp(request.Stars, 1, 5),
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
            CreatedAtUtc = now,
        };
        _db.VendorRatings.Add(rating);
        await _db.SaveChangesAsync(token);

        await RefreshRatingAggregatesAsync(vendor, token);
        await _db.SaveChangesAsync(token);

        await SafeAsync("rating broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId, VendorEntityType, vendor.Id, VendorResponse.FromEntity(vendor), token));

        return VendorRatingResponse.FromEntity(rating);
    }, ct);

    public async Task<VendorScorecardResponse?> GetScorecardAsync(WorkspaceReadScope scope, int vendorId, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var allProperties = _db.AuthorizedWorkspaceAssignments(
            scope,
            [CapabilityKeys.WorkRead],
            CapabilityAuthorizationTargetKind.Property,
            _timeProvider.UtcNow());
        var vendor = await _db.Vendors
            .AsNoTracking()
            .FirstOrDefaultAsync(v =>
                v.Id == vendorId && v.PortfolioId == portfolioId && allProperties.Any(), ct);
        if (vendor is null)
        {
            return null;
        }

        // Filtering and averaging stay in one translated SQL statement; scorecards must not load
        // every historical dispatch merely to compute one scalar.
        var avgResponseTicks = await _db.VendorDispatches
            .AsNoTracking()
            .Where(d => d.VendorId == vendorId && d.PortfolioId == portfolioId &&
                        d.Status == VendorDispatchStatus.Completed && d.RespondedAtUtc != null)
            .Select(d => (double?)(d.RespondedAtUtc!.Value.Ticks - d.DispatchedAtUtc.Ticks))
            .AverageAsync(ct);
        var avgResponseHours = avgResponseTicks.HasValue
            ? Math.Round((decimal)(avgResponseTicks.Value / TimeSpan.TicksPerHour), 2)
            : (decimal?)null;

        return new VendorScorecardResponse
        {
            VendorId = vendor.Id,
            Name = vendor.Name,
            AverageRating = vendor.AverageRating,
            RatingCount = vendor.RatingCount,
            JobsCompleted = vendor.JobsCompleted,
            AvgResponseHours = avgResponseHours,
        };
    }

    /// <summary>
    /// Recomputes the vendor's cached <c>AverageRating</c>/<c>RatingCount</c> from the rating rows.
    /// The vendor must be tracked; the caller saves. Computing from source keeps the cache exact even
    /// if a rating is ever edited/removed.
    /// </summary>
    private async Task RefreshRatingAggregatesAsync(Vendor vendor, CancellationToken ct)
    {
        var stats = await _db.VendorRatings
            .Where(r => r.VendorId == vendor.Id && r.PortfolioId == vendor.PortfolioId)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Avg = (decimal?)g.Average(r => (decimal)r.Stars) })
            .FirstOrDefaultAsync(ct);

        vendor.RatingCount = stats?.Count ?? 0;
        vendor.AverageRating = stats?.Avg is { } avg ? Math.Round(avg, 2) : null;
        vendor.UpdatedAt = _timeProvider.UtcNow();
    }

    private static string BuildJobSms(WorkOrder workOrder, string? propertyName, string? propertyAddress, string? unitNumber, string? note)
    {
        var location = propertyName ?? propertyAddress ?? "the property";
        if (!string.IsNullOrWhiteSpace(unitNumber))
        {
            location += $", Unit {unitNumber}";
        }

        var sb = new StringBuilder();
        sb.Append($"New job at {location}: {workOrder.Title}.");
        if (!string.IsNullOrWhiteSpace(workOrder.Description))
        {
            sb.Append($" {workOrder.Description.Trim()}");
        }
        sb.Append($" Priority: {workOrder.Priority}.");
        if (!string.IsNullOrWhiteSpace(note))
        {
            sb.Append($" Note: {note.Trim()}");
        }
        sb.Append(" Reply DONE when the job is complete.");
        return sb.ToString();
    }

    private async Task SafeAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vendor dispatch side effect '{Label}' failed (continuing).", label);
        }
    }
}
