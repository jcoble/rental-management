namespace RentalCommand.Engine.Services;

/// <summary>
/// Generates <see cref="Core.Entities.WorkOrder"/> rows from active recurring-maintenance tasks
/// that have come due, idempotently (at most one work order per task per period), and advances each
/// task's NextDueDate by its interval.
/// </summary>
public interface IRecurringMaintenanceService
{
    /// <summary>
    /// Scan active, non-deleted recurring-maintenance tasks whose NextDueDate is on/before the
    /// landlord's local "today", and for each create a New work order (with its initial status event)
    /// then roll NextDueDate forward by the recurrence interval. Returns the number of work orders
    /// created this run.
    /// </summary>
    Task<int> GenerateAsync(CancellationToken ct = default);
}
