using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IVendorDispatchService"/>
public class VendorDispatchService : IVendorDispatchService
{
    private const string VendorEntityType = "Vendor";
    private const string WorkOrderEntityType = "WorkOrder";
    private const string DispatchEntityType = "VendorDispatch";

    // A dispatch is still "open" (awaiting the vendor's DONE) in these statuses. Matches the set the
    // inbound-DONE handler uses to find the dispatch to close, so the idempotency guard and the closer
    // agree on what "already dispatched" means.
    private static readonly VendorDispatchStatus[] OpenStatuses =
        { VendorDispatchStatus.Dispatched, VendorDispatchStatus.Acknowledged };

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<VendorDispatchService> _logger;
    private readonly TimeProvider _timeProvider;

    public VendorDispatchService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit,
        IMessagePublisher publisher,
        ILogger<VendorDispatchService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _audit = audit;
        _publisher = publisher;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<DispatchResult> DispatchAsync(int portfolioId, int workOrderId, DispatchWorkOrderRequest request, int? changedByUserId, CancellationToken ct = default)
    {
        var workOrder = await _db.WorkOrders
            .FirstOrDefaultAsync(w => w.Id == workOrderId && w.PortfolioId == portfolioId, ct);
        if (workOrder is null)
        {
            return DispatchResult.NotFound();
        }

        var vendor = await _db.Vendors
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

        // Idempotency: if this work order already has an OPEN dispatch to this same vendor, refuse to
        // create a second one. Re-dispatching the same job to the same vendor would stack duplicate open
        // dispatches and duplicate SMS, yet a single "DONE" reply only closes the most recent — leaving
        // the rest permanently open and double-counting the vendor's job stats. Re-dispatch to a DIFFERENT
        // vendor, or after this one is completed/cancelled, is still allowed.
        var alreadyOpen = await _db.VendorDispatches
            .AnyAsync(d => d.PortfolioId == portfolioId
                && d.WorkOrderId == workOrder.Id
                && d.VendorId == vendor.Id
                && OpenStatuses.Contains(d.Status), ct);
        if (alreadyOpen)
        {
            return DispatchResult.AlreadyDispatched();
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

        // Assign the vendor + record an open dispatch in the same save.
        workOrder.VendorId = vendor.Id;
        workOrder.UpdatedAt = now;

        var dispatch = new VendorDispatch
        {
            PortfolioId = portfolioId,
            WorkOrderId = workOrder.Id,
            VendorId = vendor.Id,
            Status = VendorDispatchStatus.Dispatched,
            DispatchedAtUtc = now,
            Message = message,
        };
        _db.VendorDispatches.Add(dispatch);

        await _db.SaveChangesAsync(ct);

        // Enqueue the outbound job SMS via the outbox (Engine delivers it).
        await SafeAsync("dispatch sms", () => _publisher.PublishAsync(portfolioId, "sms", new
        {
            to = vendorPhone,
            message,
        }, ct));

        await SafeAsync("dispatch audit", () => _audit.LogAsync(
            portfolioId,
            DispatchEntityType,
            dispatch.Id,
            AuditLogOperation.Created,
            userId: changedByUserId,
            actorLabel: changedByUserId.HasValue ? null : "staff",
            newValues: JsonSerializer.Serialize(new
            {
                workOrderId = workOrder.Id,
                vendorId = vendor.Id,
                status = dispatch.Status.ToString(),
            }),
            changeReason: $"Work order #{workOrder.Id} dispatched to vendor {vendor.Name} by SMS.",
            ct: ct));

        var response = VendorDispatchResponse.FromEntity(dispatch);
        await SafeAsync("dispatch broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId, DispatchEntityType, dispatch.Id, response, ct));
        await SafeAsync("work order broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId, WorkOrderEntityType, workOrder.Id, WorkOrderResponse.FromEntity(workOrder), ct));

        return DispatchResult.Ok(response);
    }

    public async Task<VendorRatingResponse?> RateAsync(int portfolioId, int vendorId, CreateVendorRatingRequest request, CancellationToken ct = default)
    {
        var vendor = await _db.Vendors
            .FirstOrDefaultAsync(v => v.Id == vendorId && v.PortfolioId == portfolioId, ct);
        if (vendor is null)
        {
            return null;
        }

        // Keep an out-of-scope work order from being linked into this portfolio's rating.
        int? workOrderId = null;
        if (request.WorkOrderId.HasValue)
        {
            var inScope = await _db.WorkOrders
                .AnyAsync(w => w.Id == request.WorkOrderId.Value && w.PortfolioId == portfolioId, ct);
            if (inScope)
            {
                workOrderId = request.WorkOrderId.Value;
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
        await _db.SaveChangesAsync(ct);

        await RefreshRatingAggregatesAsync(vendor, ct);
        await _db.SaveChangesAsync(ct);

        await SafeAsync("rating broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId, VendorEntityType, vendor.Id, VendorResponse.FromEntity(vendor), ct));

        return VendorRatingResponse.FromEntity(rating);
    }

    public async Task<VendorScorecardResponse?> GetScorecardAsync(int portfolioId, int vendorId, CancellationToken ct = default)
    {
        var vendor = await _db.Vendors
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == vendorId && v.PortfolioId == portfolioId, ct);
        if (vendor is null)
        {
            return null;
        }

        // Average response time across completed dispatches (DispatchedAt → RespondedAt).
        var responseSpans = await _db.VendorDispatches
            .AsNoTracking()
            .Where(d => d.VendorId == vendorId && d.PortfolioId == portfolioId &&
                        d.Status == VendorDispatchStatus.Completed && d.RespondedAtUtc != null)
            .Select(d => new { d.DispatchedAtUtc, RespondedAtUtc = d.RespondedAtUtc!.Value })
            .ToListAsync(ct);

        decimal? avgResponseHours = null;
        if (responseSpans.Count > 0)
        {
            var totalHours = responseSpans
                .Sum(s => (s.RespondedAtUtc - s.DispatchedAtUtc).TotalHours);
            avgResponseHours = Math.Round((decimal)(totalHours / responseSpans.Count), 2);
        }

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
