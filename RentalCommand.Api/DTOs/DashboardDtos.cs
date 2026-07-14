namespace RentalCommand.Api.DTOs;

/// <summary>
/// Aggregated portfolio dashboard payload for the SvelteKit web app
/// (<c>GET /api/v1/portfolios/{id}/dashboard</c>). Read-only KPI rollup; never persisted.
/// Enum values are emitted as their string names (the web client types them as string unions),
/// matching the app-wide string-enum convention.
/// </summary>
public class DashboardResponse
{
    public DashboardPortfolio Portfolio { get; set; } = new();
    public DashboardOccupancy Occupancy { get; set; } = new();
    public DashboardAccounting Accounting { get; set; } = new();
    public DashboardMaintenance Maintenance { get; set; } = new();
    public DashboardLeasing Leasing { get; set; } = new();
    public IReadOnlyList<DashboardActivity> RecentActivity { get; set; } = [];
    public IReadOnlyList<DashboardAppointment> UpcomingAppointments { get; set; } = [];
}

public class DashboardPortfolio
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ManagementCompanyName { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    /// <summary>Portfolio status as its string name (e.g. <c>Active</c>).</summary>
    public string Status { get; set; } = string.Empty;
}

public class DashboardOccupancy
{
    public int TotalUnits { get; set; }
    public int OccupiedUnits { get; set; }
    public int VacantUnits { get; set; }
    public int ReservedUnits { get; set; }
    /// <summary>Occupied units as a percentage of total units (0-100, one decimal place).</summary>
    public double OccupancyRate { get; set; }
}

public class DashboardAccounting
{
    public decimal DueThisMonthAmount { get; set; }
    public decimal PaidThisMonthAmount { get; set; }
    /// <summary>Sum of still-owed payments whose due date is in the past.</summary>
    public decimal OverdueAmount { get; set; }
    public decimal ExpensesThisMonthAmount { get; set; }
    /// <summary>Rent collected this month minus expenses incurred this month.</summary>
    public decimal NetThisMonth { get; set; }
}

public class DashboardMaintenance
{
    /// <summary>Work orders that are not Completed, Cancelled, or Archived.</summary>
    public int OpenCount { get; set; }
    /// <summary>Open work orders with Emergency priority.</summary>
    public int EmergencyCount { get; set; }
    public int InProgressCount { get; set; }
}

public class DashboardLeasing
{
    /// <summary>Continuous household/account relationships, not immutable agreement-version rows.</summary>
    public int TotalLeases { get; set; }
    /// <summary>Relationships currently occupied or ending.</summary>
    public int ActiveLeases { get; set; }
    /// <summary>Governing agreements ending within 60 business days, soonest first.</summary>
    public IReadOnlyList<DashboardExpiringLease> ExpiringSoon { get; set; } = [];
    /// <summary>Relationship counts keyed by database-derived lifecycle name.</summary>
    public IReadOnlyDictionary<string, int> ByStatus { get; set; } = new Dictionary<string, int>();
}

public class DashboardExpiringLease
{
    public int Id { get; set; }
    public string LeaseNumber { get; set; } = string.Empty;
    public string? Tenant { get; set; }
    public string? Property { get; set; }
    public string? Unit { get; set; }
    public DateTime EndDate { get; set; }
    public decimal MonthlyRent { get; set; }
}

public class DashboardActivity
{
    public long Id { get; set; }
    /// <summary>Activity type as its string name (e.g. <c>PaymentRecorded</c>).</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Primary key of the entity this row touched, so the web can deep-link to its detail page.</summary>
    public int EntityId { get; set; }
    /// <summary>
    /// Unit that owns this activity when the touched entity belongs to a unit. Lets secondary dashboard
    /// links land inside the unit Command Center tab instead of a generic detail page.
    /// </summary>
    public int? UnitId { get; set; }
    public string? Action { get; set; }
    public string? Description { get; set; }
    /// <summary>
    /// Human label naming the specific record this row touched (e.g. the tenant's name or the
    /// work-order title), composed alongside <see cref="Description"/>. Null when the entity type has
    /// no cheap DB-side label; the web then shows the verb-only description.
    /// </summary>
    public string? Label { get; set; }
    public string? Actor { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class DashboardAppointment
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>Appointment type as its string name (e.g. <c>Showing</c>).</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Appointment status as its string name (e.g. <c>Scheduled</c>).</summary>
    public string Status { get; set; } = string.Empty;
    public DateTime ScheduledStart { get; set; }
    public string? AssignedTo { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
}
