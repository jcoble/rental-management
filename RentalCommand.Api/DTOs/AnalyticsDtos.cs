namespace RentalCommand.Api.DTOs;

/// <summary>
/// A single month's income / expense / net data point for the 12-month trend chart.
/// <c>Month</c> is formatted as <c>yyyy-MM</c> (oldest first).
/// </summary>
public record MonthlyPoint(string Month, decimal Income, decimal Expenses, decimal Net);

/// <summary>Count + total amount for a bucket of payments (e.g. overdue rent).</summary>
public record CountAmount(int Count, decimal Amount);

/// <summary>Open work order count for a single priority bucket.</summary>
public record PriorityCount(string Priority, int Count);

/// <summary>
/// Full portfolio analytics overview returned by <c>GET /api/v1/analytics/overview</c>.
/// All monetary values are rounded to 2 dp. Rates are 0-100 with one decimal place.
/// </summary>
public class AnalyticsOverview
{
    // ── Occupancy ──────────────────────────────────────────────────────────────────────────────────
    public int TotalUnits { get; set; }
    public int OccupiedUnits { get; set; }
    /// <summary>Occupied ÷ total × 100, one decimal place. 0 when there are no units.</summary>
    public decimal OccupancyRate { get; set; }

    // ── This-month rent ────────────────────────────────────────────────────────────────────────────
    /// <summary>Current-month ledger <c>RentCharge</c> obligations.</summary>
    public decimal MonthRentScheduled { get; set; }
    /// <summary>Sum of Rent payments with Status==Paid and PaidDate in the current UTC month.</summary>
    public decimal MonthRentCollected { get; set; }
    /// <summary>Collected ÷ scheduled × 100, one decimal place. 0 when nothing is scheduled.</summary>
    public decimal CollectionRate { get; set; }

    // ── Overdue ────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Rent payments in {Scheduled, Late, Partial} with DueDate before today.</summary>
    public CountAmount Overdue { get; set; } = new(0, 0);

    // ── 12-month trend ─────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Income/expense/net for each of the last 12 calendar months, oldest first.
    /// Income = Paid Rent payments with PaidDate in that month.
    /// Expenses = portfolio expenses with IncurredAt in that month.
    /// </summary>
    public IReadOnlyList<MonthlyPoint> Trend { get; set; } = [];

    // ── Lease expiry ───────────────────────────────────────────────────────────────────────────────
    /// <summary>Active leases with EndDate within the next 30 days (inclusive).</summary>
    public int LeasesExpiring30 { get; set; }
    /// <summary>Active leases with EndDate within the next 60 days (cumulative).</summary>
    public int LeasesExpiring60 { get; set; }
    /// <summary>Active leases with EndDate within the next 90 days (cumulative).</summary>
    public int LeasesExpiring90 { get; set; }

    // ── Work orders ────────────────────────────────────────────────────────────────────────────────
    /// <summary>Count of open (not Completed / Cancelled / Archived) work orders, grouped by Priority.</summary>
    public IReadOnlyList<PriorityCount> OpenWorkOrders { get; set; } = [];

    // ── Monthly recurring rent ─────────────────────────────────────────────────────────────────────
    /// <summary>Currently governing agreement base rent plus active recurring-rent addenda.</summary>
    public decimal MonthlyRecurringRent { get; set; }
}
