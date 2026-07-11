using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Automation;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Turns standing recurring-maintenance chores into work orders. For each active, non-deleted task
/// whose NextDueDate has arrived (the landlord's LOCAL "today"), it creates one New
/// <see cref="WorkOrder"/> + its initial <see cref="WorkOrderStatusEvent"/>, stamps
/// <see cref="RecurringMaintenanceTask.LastGeneratedAtUtc"/>, then advances NextDueDate by the
/// recurrence interval. Runs inside a fresh DI scope (scoped <see cref="RentalCommandDbContext"/>),
/// under the Engine's single-instance advisory lock.
/// </summary>
public sealed class RecurringMaintenanceService : IRecurringMaintenanceService
{
    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;
    private readonly IAppTimeZoneProvider _tz;
    private readonly IScheduledAutomationClaimStore _claims;
    private readonly ILogger<RecurringMaintenanceService> _logger;

    public RecurringMaintenanceService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        IAppTimeZoneProvider tz,
        IScheduledAutomationClaimStore claims,
        ILogger<RecurringMaintenanceService> logger)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _tz = tz;
        _claims = claims;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        // Chores roll over on the landlord's local calendar day, not in UTC. Near midnight an evening
        // (ET) UtcNow is already "tomorrow" in UTC, which would generate a due task a day early — so the
        // due-date comparison is done in this zone (resolved once per cycle so a sim-clock tz override
        // still takes effect). Every value WRITTEN to the DB stays UTC.
        var businessTimeZone = _tz.BusinessTimeZone;

        // Business "today" in the landlord's local zone (drives the due comparison + interval math only).
        // NextDueDate is a timestamptz column, so the comparison value must be UTC-Kind or Npgsql rejects
        // the parameter ("Cannot write DateTime with Kind=Unspecified to timestamp with time zone").
        // NextDueDate is stored as UTC-midnight of the local calendar date, so we mark this the same way.
        var today = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.UtcNow(), businessTimeZone).Date,
            DateTimeKind.Utc);

        var claims = await _claims.ClaimRecurringMaintenanceAsync(
            $"{Environment.MachineName}:recurring-maintenance", today, _timeProvider.UtcNow(),
            TimeSpan.FromMinutes(6), 25, ct);
        var created = await ProcessClaimsAsync(claims, today, businessTimeZone, ct);

        if (created > 0)
        {
            _logger.LogInformation(
                "RecurringMaintenanceService created {Count} work order(s) from recurring maintenance schedules.",
                created);
        }

        return created;
    }

    private async Task<int> ProcessClaimsAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims,
        DateTime today,
        TimeZoneInfo businessTimeZone,
        CancellationToken ct)
    {
        if (claims.Count == 0) return 0;
        var committed = new List<WorkOrder>();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var tasks = await _claims.LockOwnedRecurringMaintenanceAsync(claims, today, ct);
            foreach (var task in tasks)
            {
                ct.ThrowIfCancellationRequested();

                var now = _timeProvider.UtcNow();

                // Roll NextDueDate forward from the due value. If several periods were missed, create
                // one work order and advance beyond today so the field queue is not flooded.
                var nextDue = task.NextDueDate;
                do
                {
                    nextDue = Advance(nextDue, task.RecurrenceInterval);
                }
                while (nextDue <= today);

                var workOrder = new WorkOrder
                {
                    PortfolioId = task.PortfolioId,
                    PropertyId = task.PropertyId,
                    UnitId = task.UnitId,
                    VendorId = task.VendorId,
                    RecurringMaintenanceTaskId = task.Id,
                    Title = task.Title,
                    Description = string.IsNullOrWhiteSpace(task.Description) ? task.Title : task.Description,
                    Category = string.IsNullOrWhiteSpace(task.Category) ? "General" : task.Category,
                    Priority = task.Priority,
                    Status = WorkOrderStatus.New,
                    RequestedAt = now,
                    ScheduledFor = ToScheduledUtc(task.NextDueDate, task.ScheduledTime, businessTimeZone),
                    EstimatedCost = task.EstimatedCost,
                    CreatedBy = "Recurring maintenance",
                    UpdatedAt = now,
                };

                // Initial timeline entry is in the same save so it cannot diverge from current status.
                workOrder.StatusEvents.Add(new WorkOrderStatusEvent
                {
                    PortfolioId = task.PortfolioId,
                    FromStatus = null,
                    ToStatus = WorkOrderStatus.New,
                    Note = "Auto-created from recurring maintenance schedule",
                    ChangedByUserId = null,
                    ChangedByLabel = "System",
                    CreatedAtUtc = now,
                });

                _db.WorkOrders.Add(workOrder);
                committed.Add(workOrder);

                // Work-order inserts and schedule advances commit atomically for the claimed batch.
                task.LastGeneratedAtUtc = now;
                task.NextDueDate = nextDue;
                task.UpdatedAt = now;
                ClearClaim(task);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            _logger.LogWarning(
                ex,
                "Failed recurring-maintenance batch; leases will expire for retry");
            return 0;
        }

        // Realtime is a post-commit hint. A transient NOTIFY failure must never roll back or duplicate
        // the durable work order/schedule advance; the next read still sees the committed record.
        foreach (var workOrder in committed)
        {
            try
            {
                await _dataUpdate.BroadcastEntityUpdateAsync(
                    workOrder.PortfolioId, "WorkOrder", workOrder.Id, WorkOrderResponse.FromEntity(workOrder), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Work order {WorkOrderId} committed but realtime broadcast failed", workOrder.Id);
            }
        }

        return committed.Count;
    }

    private static void ClearClaim(RecurringMaintenanceTask task)
    {
        task.WorkerClaimOwner = null;
        task.WorkerClaimToken = null;
        task.WorkerClaimExpiresAtUtc = null;
    }

    /// <summary>Advance a due date by one recurrence period (Weekly = +7d, else calendar months/years).</summary>
    private static DateTime Advance(DateTime date, RecurrenceInterval interval) => interval switch
    {
        RecurrenceInterval.Weekly => date.AddDays(7),
        RecurrenceInterval.Monthly => date.AddMonths(1),
        RecurrenceInterval.Quarterly => date.AddMonths(3),
        RecurrenceInterval.SemiAnnually => date.AddMonths(6),
        RecurrenceInterval.Annually => date.AddYears(1),
        _ => date.AddMonths(1),
    };

    private static DateTime? ToScheduledUtc(DateTime dueDate, TimeOnly? scheduledTime, TimeZoneInfo businessTimeZone)
    {
        if (scheduledTime == null)
            return null;

        var localDateTime = dueDate.Date.Add(scheduledTime.Value.ToTimeSpan());
        localDateTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localDateTime, businessTimeZone);
    }
}
