namespace RentalCommand.Api.DTOs;

/// <summary>
/// At-a-glance aggregate for the Unit Command Center page (spec section 8). Mirrors the portfolio
/// dashboard pattern: a single endpoint returns the header chips, current lease/tenant, the derived
/// lifecycle stage + next-best-action, a small capped overview, and a short recent-timeline slice.
/// Per-tab heavy data is NOT here — tabs lazy-load via the existing filtered list endpoints — so
/// opening a unit stays fast regardless of history size.
/// </summary>
public class UnitDashboardResponse
{
    /// <summary>The unit itself (same shape the unit list/detail endpoints return).</summary>
    public UnitResponse Unit { get; set; } = new();

    /// <summary>Name of the unit's property, for the header breadcrumb.</summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>Derived lifecycle stage (string name, e.g. <c>Active</c>); see the stage resolver.</summary>
    public string LifecycleStage { get; set; } = string.Empty;

    /// <summary>The single recommended action for the current stage, with a deep link.</summary>
    public UnitNextBestAction NextBestAction { get; set; } = new();

    /// <summary>The compact health summary rendered as chips in the page header.</summary>
    public UnitDashboardHeader Header { get; set; } = new();

    /// <summary>The unit's current lease (most-recent Active, else latest), null if never leased.</summary>
    public UnitLeaseSummary? CurrentLease { get; set; }

    /// <summary>The current lease's tenant, null when there is no current lease.</summary>
    public UnitTenantSummary? CurrentTenant { get; set; }

    /// <summary>Small, capped lists for the Overview tab (each ~5 rows).</summary>
    public UnitDashboardOverview Overview { get; set; } = new();

    /// <summary>The most recent unit history events (~15), for the persistent timeline rail.</summary>
    public IReadOnlyList<AuditEntryResponse> RecentTimeline { get; set; } = [];
}

/// <summary>A stage's recommended next action plus the web route it deep-links to.</summary>
public class UnitNextBestAction
{
    public string Label { get; set; } = string.Empty;

    /// <summary>Web route to act on it (e.g. the rent tab, or a list filtered to this unit).</summary>
    public string Href { get; set; } = string.Empty;
}

/// <summary>The header health chips (all computed DB-side).</summary>
public class UnitDashboardHeader
{
    /// <summary>One-word rent state for the chip: <c>Overdue</c>, <c>Due</c>, <c>Current</c>, or <c>NoLease</c>.</summary>
    public string RentState { get; set; } = "NoLease";

    /// <summary>Outstanding (still-owed) rent for the current lease — drives the rent chip / Collect action.</summary>
    public decimal OutstandingRentBalance { get; set; }

    /// <summary>Count of open (not Completed/Cancelled/Archived) work orders on the unit.</summary>
    public int OpenWorkOrderCount { get; set; }

    /// <summary>Days until the current lease ends; null when there is no current lease end date.</summary>
    public int? LeaseEndsInDays { get; set; }

    /// <summary>
    /// Documents on file for the unit and its child records (lease, work orders, expenses, payments, inspections).
    /// NOTE: scan drafts are not unit-scoped until confirmed, so this is the unit's document count
    /// (what the Documents tab lists) rather than a strict "pending AI review" queue.
    /// </summary>
    public int DocsNeedingReviewCount { get; set; }

    /// <summary>Current tenant's display name, null when vacant.</summary>
    public string? CurrentTenantName { get; set; }
}

/// <summary>Compact current-lease projection for the header / overview.</summary>
public class UnitLeaseSummary
{
    public int Id { get; set; }
    public string LeaseNumber { get; set; } = string.Empty;

    /// <summary>Lease status as its string name (e.g. <c>Active</c>).</summary>
    public string Status { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal MonthlyRent { get; set; }
    public decimal SecurityDeposit { get; set; }
}

/// <summary>Compact current-tenant projection.</summary>
public class UnitTenantSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

/// <summary>The Overview tab's capped lists (each ~5 rows, newest/most-relevant first).</summary>
public class UnitDashboardOverview
{
    public IReadOnlyList<UnitPaymentSummary> RecentPayments { get; set; } = [];
    public IReadOnlyList<UnitWorkOrderSummary> OpenWorkOrders { get; set; } = [];
    public IReadOnlyList<UnitDocumentSummary> PendingDocs { get; set; } = [];
    public IReadOnlyList<UnitAppointmentSummary> UpcomingAppointments { get; set; } = [];
}

public class UnitPaymentSummary
{
    public int Id { get; set; }
    public int LeaseId { get; set; }

    /// <summary>Payment type as its string name (e.g. <c>Rent</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Payment status as its string name (e.g. <c>Paid</c>).</summary>
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidDate { get; set; }
}

public class UnitWorkOrderSummary
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Status as its string name (e.g. <c>InProgress</c>).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Priority as its string name (e.g. <c>Emergency</c>).</summary>
    public string Priority { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
}

public class UnitDocumentSummary
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The kind of record the file is attached to (e.g. <c>Lease</c>, <c>WorkOrder</c>).</summary>
    public string? EntityType { get; set; }
    public int? EntityId { get; set; }
    public DateTime UploadedAt { get; set; }
}

public class UnitAppointmentSummary
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Type as its string name (e.g. <c>Showing</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Status as its string name (e.g. <c>Scheduled</c>).</summary>
    public string Status { get; set; } = string.Empty;
    public DateTime ScheduledStart { get; set; }
    public string? AssignedTo { get; set; }
}
