using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Operations;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IVendorDispatchService"/>
public class VendorDispatchService : IVendorDispatchService
{
    private const string WorkOrderEntityType = "WorkOrder";
    private const string DispatchEntityType = "VendorDispatch";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IRequestWriteExecutor _writes;
    private readonly ILogger<VendorDispatchService> _logger;
    private readonly TimeProvider _timeProvider;

    public VendorDispatchService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IRequestWriteExecutor writes,
        ILogger<VendorDispatchService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _writes = writes;
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

        var operationKey = $"{portfolioId}:{workOrderId}:{request.IdempotencyKey.Trim()}";
        var command = new DispatchWorkOrderToVendorCommand(
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
                    : null);
        var outcome = await _writes.ExecuteAsync(
            operationKey, DispatchWorkOrderToVendorHandler.Write(command, _db), ct);
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

    public async Task<CancelDispatchResult> CancelAuthorizedAsync(
        WorkspaceReadScope scope,
        int workOrderId,
        int dispatchId,
        CancelVendorDispatchRequest request,
        int? changedByUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        var operationKey = $"{scope.PortfolioId}:{workOrderId}:{dispatchId}:{request.IdempotencyKey.Trim()}";
        var now = _timeProvider.UtcNow();
        var command = new CancelVendorDispatchCommand(
                scope.PortfolioId,
                workOrderId,
                dispatchId,
                changedByUserId,
                request.Reason ?? "Vendor dispatch cancelled.",
                now,
                operationKey,
                new DispatchManagementAccess(
                    scope.SessionId,
                    scope.UserId,
                    scope.AccessContextId,
                    scope.AccessRevision));
        var outcome = await _writes.ExecuteAsync(
            operationKey, CancelVendorDispatchHandler.Write(command, _db), ct);

        if (outcome.Value.Outcome == CancelVendorDispatchOutcome.NotFound)
        {
            return CancelDispatchResult.NotFound();
        }

        var response = new CancelVendorDispatchResponse
        {
            Id = outcome.Value.DispatchId,
            PortfolioId = outcome.Value.PortfolioId,
            WorkOrderId = outcome.Value.WorkOrderId,
            VendorId = outcome.Value.VendorId,
            Status = outcome.Value.Status,
            CancelledAtUtc = outcome.Value.CancelledAtUtc,
            Reason = outcome.Value.Reason,
            Replayed = outcome.Disposition == AtomicCommandDisposition.Replayed,
        };
        await SafeAsync("dispatch cancellation broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
            scope.PortfolioId, DispatchEntityType, response.Id, response, ct));
        return outcome.Value.Outcome == CancelVendorDispatchOutcome.AlreadyClosed
            ? CancelDispatchResult.AlreadyClosed(response)
            : CancelDispatchResult.Ok(response);
    }

    public async Task<RecoverVendorDispatchChronologyResponse> RecoverChronologyAuthorizedAsync(
        WorkspaceReadScope scope,
        int workOrderId,
        int dispatchId,
        RecoverVendorDispatchChronologyRequest request,
        int actorUserId,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey.Trim())))
            .ToLowerInvariant();
        var deliveryKey =
            $"vendor-dispatch-chronology:{scope.PortfolioId}:{workOrderId}:{dispatchId}:{digest}";
        var command = new RecoverVendorDispatchChronologyCommand(
            scope.PortfolioId,
            workOrderId,
            dispatchId,
            request.ExpectedContaminatedDispatchedAtUtc,
            request.ExpectedStatusEventId,
            request.ExpectedOutboxId,
            request.ExpectedOutboxIdempotencyKey.Trim(),
            request.OriginalCommandIdempotencyKey.Trim(),
            request.CorrectDispatchedAtUtc,
            actorUserId,
            new DispatchManagementAccess(
                scope.SessionId,
                scope.UserId,
                scope.AccessContextId,
                scope.AccessRevision),
            deliveryKey);
        var outcome = await _writes.ExecuteAsync(
            deliveryKey, RecoverVendorDispatchChronologyHandler.Write(command, _db), ct);
        return new RecoverVendorDispatchChronologyResponse
        {
            WorkOrderId = outcome.Value.WorkOrderId,
            DispatchId = outcome.Value.DispatchId,
            StatusEventId = outcome.Value.StatusEventId,
            OutboxId = outcome.Value.OutboxId,
            DispatchedAtUtc = outcome.Value.DispatchedAtUtc,
            WorkOrderUpdatedAtRepaired = outcome.Value.WorkOrderUpdatedAtRepaired,
            Replayed = outcome.Disposition == AtomicCommandDisposition.Replayed,
        };
    }

    public async Task<VendorRatingResponse?> RateAsync(
        WorkspaceReadScope scope,
        int vendorId,
        CreateVendorRatingRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var command = new CreateVendorRatingCommand(
            scope.PortfolioId,
            new StaffOperationActor(
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision),
            vendorId,
            request.WorkOrderId,
            request.Stars,
            request.Comment,
            _timeProvider.UtcNow(),
            idempotencyKey);
        var outcome = await _writes.ExecuteAsync(
            Identity("vendor-rating.create", idempotencyKey).IdempotencyKey,
            CreateVendorRatingHandler.Write(command, _db), ct);
        return outcome.Value.Outcome == OperationMutationOutcome.NotFound ||
               outcome.Value.ResponseJson is null
            ? null
            : JsonSerializer.Deserialize<VendorRatingResponse>(outcome.Value.ResponseJson);
    }

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
        var completedResponseTimes = _db.VendorDispatches
            .AsNoTracking()
            .Where(d => d.VendorId == vendorId && d.PortfolioId == portfolioId &&
                        d.Status == VendorDispatchStatus.Completed && d.RespondedAtUtc != null);

        // PostgreSQL translates DateTime subtraction and TimeSpan.TotalHours directly. DateTime.Ticks
        // is not translated by Npgsql and caused the live scorecard endpoint to return 500. SQLite is
        // retained only as the test-provider expression; both paths still average in one DB query.
        var avgResponseHoursRaw = _db.Database.IsNpgsql()
            ? await completedResponseTimes
                .Select(d => (double?)((d.RespondedAtUtc!.Value - d.DispatchedAtUtc).TotalHours))
                .AverageAsync(ct)
            : await completedResponseTimes
                .Select(d => (double?)(d.RespondedAtUtc!.Value.Ticks - d.DispatchedAtUtc.Ticks))
                .AverageAsync(ct) / TimeSpan.TicksPerHour;
        var avgResponseHours = avgResponseHoursRaw.HasValue
            ? Math.Round((decimal)avgResponseHoursRaw.Value, 2)
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

    private static AtomicCommandIdentity Identity(string operation, string key)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return new AtomicCommandIdentity(operation, digest);
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
