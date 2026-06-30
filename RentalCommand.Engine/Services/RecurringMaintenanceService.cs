using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

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
    private const string DefaultTimeZoneId = "America/New_York";

    private readonly RentalCommandDbContext _db;
    private readonly INotificationSettingsService _settings;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeZoneInfo _businessTimeZone;
    private readonly ILogger<RecurringMaintenanceService> _logger;

    public RecurringMaintenanceService(
        RentalCommandDbContext db,
        INotificationSettingsService settings,
        IDataUpdateService dataUpdate,
        IConfiguration configuration,
        ILogger<RecurringMaintenanceService> logger)
    {
        _db = db;
        _settings = settings;
        _dataUpdate = dataUpdate;
        _logger = logger;

        // Chores roll over on the landlord's local calendar day, not in UTC. Near midnight an evening
        // (ET) UtcNow is already "tomorrow" in UTC, which would generate a due task a day early — so the
        // due-date comparison is done in this zone. Every value WRITTEN to the DB stays UTC.
        var tzId = configuration["App:TimeZone"];
        if (string.IsNullOrWhiteSpace(tzId))
            tzId = DefaultTimeZoneId;
        _businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        // Business "today" in the landlord's local zone (drives the due comparison + interval math only).
        // NextDueDate is a timestamptz column, so the comparison value must be UTC-Kind or Npgsql rejects
        // the parameter ("Cannot write DateTime with Kind=Unspecified to timestamp with time zone").
        // NextDueDate is stored as UTC-midnight of the local calendar date, so we mark this the same way.
        var today = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _businessTimeZone).Date,
            DateTimeKind.Utc);

        // Active, non-deleted tasks that are due. The query filter already excludes soft-deleted rows.
        var tasks = await _db.RecurringMaintenanceTasks
            .Where(t => t.IsActive && t.NextDueDate <= today)
            .ToListAsync(ct);

        var configCache = new Dictionary<int, NotificationsConfig>();
        var created = 0;

        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();

            // Settings are per-portfolio. The master EnableRecurringMaintenance flag is the outer gate;
            // the per-task IsActive (filtered above) is the real switch.
            if (!configCache.TryGetValue(task.PortfolioId, out var cfg))
            {
                cfg = await _settings.GetRuntimeAsync(task.PortfolioId, ct);
                configCache[task.PortfolioId] = cfg;
            }

            if (!cfg.EnableRecurringMaintenance)
                continue;

            var now = DateTime.UtcNow;

            // Roll NextDueDate forward off the value that was due. If several periods were missed (e.g. the
            // Engine was down), we still create exactly ONE work order this run and advance the schedule
            // past "today" so the field queue isn't flooded with a backlog — the next due date is then
            // back on cadence.
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
                ScheduledFor = ToScheduledUtc(task.NextDueDate, task.ScheduledTime, _businessTimeZone),
                EstimatedCost = task.EstimatedCost,
                CreatedBy = "Recurring maintenance",
                UpdatedAt = now,
            };

            // Initial timeline entry: null → New, in the same save as the work order so the stream can
            // never diverge from the current status. ChangedByLabel = "System" (a worker generated it).
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

            // The work-order insert and the schedule advance must commit atomically: a crash between two
            // separate saves could create a work order without rolling NextDueDate forward, double-billing
            // the chore on the next cycle. Wrap both in one transaction; the (active, due) re-scan retries
            // the whole unit if it rolls back.
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                task.LastGeneratedAtUtc = now;
                task.NextDueDate = nextDue;
                task.UpdatedAt = now;

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                created++;

                await _dataUpdate.BroadcastEntityUpdateAsync(
                    task.PortfolioId, "WorkOrder", workOrder.Id, WorkOrderResponse.FromEntity(workOrder), ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(
                    ex,
                    "Failed to generate work order for recurring maintenance task {TaskId} (portfolio {PortfolioId}); rolled back, will retry",
                    task.Id, task.PortfolioId);

                // Detach so the context stays usable for the next iteration.
                _db.Entry(workOrder).State = EntityState.Detached;
                continue;
            }
        }

        if (created > 0)
        {
            _logger.LogInformation(
                "RecurringMaintenanceService created {Count} work order(s) from recurring maintenance schedules.",
                created);
        }

        return created;
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
