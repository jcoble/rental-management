using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Operations;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISmsInboundVendorDoneService"/>
public sealed class SmsInboundVendorDoneService : ISmsInboundVendorDoneService
{
    private const string WorkOrderEntityType = "WorkOrder";
    private const string DispatchEntityType = "VendorDispatch";
    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IWriteExecutor _writes;
    private readonly ILogger<SmsInboundVendorDoneService> _logger;

    public SmsInboundVendorDoneService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IWriteExecutor writes,
        ILogger<SmsInboundVendorDoneService> logger)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _writes = writes;
        _logger = logger;
    }

    public async Task<SmsInboundVendorDoneResult> TryHandleAsync(
        string providerEventId,
        string? fromPhone,
        string? body,
        DateTime receivedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerEventId);
        var completionRequest = IsDone(body);
        var normalizedFrom = SmsPhone.Normalize(fromPhone) ?? string.Empty;

        var identity = new AtomicCommandIdentity(
            "sms.vendor-done",
            $"provider-event:{Hash(providerEventId.Trim())}");
        var command = new CompleteVendorDispatchFromInboundCommand(
                providerEventId.Trim(),
                normalizedFrom,
                completionRequest,
                DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc));
        var outcome = await _writes.ExecuteAsync(
            identity.IdempotencyKey,
            CompleteVendorDispatchFromInboundRule.Write(command, _db), ct);

        if (outcome.Value.Outcome == CompleteVendorDispatchFromInboundOutcome.NoOpenDispatch)
        {
            return new SmsInboundVendorDoneResult(
                false,
                null,
                completionRequest
                    ? "We could not match this number to an open job."
                    : "Reply DONE when the job is complete.");
        }

        await BroadcastCommittedResultAsync(outcome.Value, ct);
        return new SmsInboundVendorDoneResult(
            true,
            outcome.Value.WorkOrderId,
            "Thanks. We marked the job complete.");
    }

    private async Task BroadcastCommittedResultAsync(
        CompleteVendorDispatchFromInboundResult result,
        CancellationToken ct)
    {
        var committed = await _db.VendorDispatches
            .AsNoTracking()
            .Where(dispatch => dispatch.Id == result.DispatchId
                && dispatch.PortfolioId == result.PortfolioId)
            .Select(dispatch => new
            {
                Dispatch = dispatch,
                WorkOrder = dispatch.WorkOrder,
            })
            .SingleOrDefaultAsync(ct);
        if (committed is not null)
        {
            if (committed.WorkOrder is not null)
            {
                await SafeAsync("work order broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
                    result.PortfolioId,
                    WorkOrderEntityType,
                    committed.WorkOrder.Id,
                    WorkOrderResponse.FromEntity(committed.WorkOrder),
                    ct));
            }

            await SafeAsync("dispatch broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
                result.PortfolioId,
                DispatchEntityType,
                committed.Dispatch.Id,
                VendorDispatchResponse.FromEntity(committed.Dispatch),
                ct));
        }

        if (result.NotificationIds.Count == 0) return;
        var notifications = await _db.Notifications
            .AsNoTracking()
            .Where(notification => notification.PortfolioId == result.PortfolioId
                && result.NotificationIds.Contains(notification.Id))
            .OrderBy(notification => notification.Id)
            .ToListAsync(ct);
        foreach (var notification in notifications)
        {
            await SafeAsync("notification broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
                result.PortfolioId,
                nameof(Notification),
                notification.Id,
                NotificationResponse.FromEntity(notification),
                ct));
        }
    }

    /// <summary>Deterministic DONE detection — literal completion keywords only, no LLM.</summary>
    private static bool IsDone(string? body)
    {
        var normalized = body?.Trim().Trim('.', '!', '?', ',').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        string[] keywords =
        [
            "DONE", "COMPLETE", "COMPLETED", "FINISHED", "FINISH", "JOB DONE", "ALL DONE", "DONE NOW",
        ];
        return keywords.Contains(normalized)
            || normalized.StartsWith("DONE ", StringComparison.Ordinal)
            || normalized.StartsWith("COMPLETE ", StringComparison.Ordinal)
            || normalized.StartsWith("COMPLETED ", StringComparison.Ordinal)
            || normalized.StartsWith("FINISHED ", StringComparison.Ordinal);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private async Task SafeAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMS vendor-done side effect '{Label}' failed (continuing).", label);
        }
    }
}
