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

    /// <summary>Canonical occupancy and possession truth, independent of Agreement state.</summary>
    public UnitOccupancyPossessionCondition OccupancyPossession { get; set; } = new();

    /// <summary>Canonical marketing availability, independent of tenancy and Agreement state.</summary>
    public UnitMarketingAvailabilityCondition MarketingAvailability { get; set; } = new();

    /// <summary>Canonical TenantAccount condition, independent of Agreement state.</summary>
    public UnitTenantAccountCondition TenantAccountCondition { get; set; } = new();

    /// <summary>Canonical legal and notice condition, without inferring other condition families.</summary>
    public UnitLegalNoticeCondition LegalNoticeCondition { get; set; } = new();

    /// <summary>Canonical maintenance and turnover condition.</summary>
    public UnitMaintenanceTurnoverCondition MaintenanceTurnover { get; set; } = new();

    /// <summary>The canonical current/planned LeaseManagement identity, independent of Agreement state.</summary>
    public int? LeaseManagementId { get; set; }

    /// <summary>The canonical open TenantAccount identity, independent of Agreement state.</summary>
    public int? TenantAccountId { get; set; }

    /// <summary>The governing or upcoming immutable agreement for the Unit's current relationship.</summary>
    public UnitLeaseSummary? CurrentLease { get; set; }

    /// <summary>The current relationship's primary tenant, null when there is no current relationship.</summary>
    public UnitTenantSummary? CurrentTenant { get; set; }

    /// <summary>All currently effective household parties, primary tenant first.</summary>
    public IReadOnlyList<UnitTenantSummary> CurrentTenants { get; set; } = [];

    /// <summary>Small, capped lists for the Overview tab (each ~5 rows).</summary>
    public UnitDashboardOverview Overview { get; set; } = new();

    /// <summary>Make-ready / turnover workspace summary, computed server-side from existing unit jobs and receipts.</summary>
    public UnitTurnoverSummary Turnover { get; set; } = new();

    /// <summary>The most recent unit history events (~15), for the persistent timeline rail.</summary>
    public IReadOnlyList<AuditEntryResponse> RecentTimeline { get; set; } = [];
}

public class UnitOccupancyPossessionCondition
{
    public string Status { get; set; } = "Vacant";
    public bool IsOccupied { get; set; }
    public bool HasScheduledMoveIn { get; set; }
    public int? LeaseManagementId { get; set; }
}

public class UnitMarketingAvailabilityCondition
{
    public string Status { get; set; } = "Available";
    public bool IsAvailable { get; set; }
}

public class UnitTenantAccountCondition
{
    public string Status { get; set; } = "NoAccount";
    public int? TenantAccountId { get; set; }
    public decimal ReceivableBalance { get; set; }
    public decimal PastDueAmount { get; set; }
}

public class UnitLegalNoticeCondition
{
    public string Status { get; set; } = "NoGoverningAgreement";
    public int? AgreementId { get; set; }
    public string? AgreementStatus { get; set; }
    public int OpenNoticeCount { get; set; }
}

public class UnitMaintenanceTurnoverCondition
{
    public string Status { get; set; } = "Clear";
    public int OpenWorkOrderCount { get; set; }
    public bool IsInTurnover { get; set; }
    public bool IsOutOfService { get; set; }
    public bool IsOnManagementHold { get; set; }
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

    /// <summary>Outstanding (still-owed) rent on the current tenant account — drives the rent chip / Collect action.</summary>
    public decimal OutstandingRentBalance { get; set; }

    /// <summary>Count of open (not Completed/Cancelled/Archived) work orders on the unit.</summary>
    public int OpenWorkOrderCount { get; set; }

    /// <summary>Days until the current lease ends; null when there is no current lease end date.</summary>
    public int? LeaseEndsInDays { get; set; }

    /// <summary>
    /// Documents on file for the unit and its related records (agreements, work orders, expenses, ledger entries, inspections).
    /// NOTE: scan drafts are not unit-scoped until confirmed, so this is the unit's document count
    /// (what the Documents tab lists) rather than a strict "pending AI review" queue.
    /// </summary>
    public int DocsNeedingReviewCount { get; set; }

    /// <summary>Current tenant's display name, null when vacant.</summary>
    public string? CurrentTenantName { get; set; }
}

/// <summary>
/// Unit make-ready workspace KPIs. This is intentionally a server projection over today's records rather
/// than a persisted Turnover entity; it gives the UI a real workspace while preserving historical data until
/// a later explicit turnover-cycle table is specified.
/// </summary>
public class UnitTurnoverSummary
{
    /// <summary>Simple operational status for the workspace header.</summary>
    public string Status { get; set; } = "NotStarted";

    /// <summary>All work orders attached to this unit.</summary>
    public int TotalTaskCount { get; set; }

    /// <summary>Work orders still requiring action.</summary>
    public int OpenTaskCount { get; set; }

    /// <summary>Completed work orders attached to this unit.</summary>
    public int CompletedTaskCount { get; set; }

    /// <summary>Expenses/receipts attached directly to the unit or to its work orders.</summary>
    public int ReceiptCount { get; set; }

    /// <summary>Sum of work-order estimates, computed in SQL.</summary>
    public decimal EstimatedCost { get; set; }

    /// <summary>Actual money spent: work-order actuals when entered plus linked expenses/receipts.</summary>
    public decimal ActualCost { get; set; }

    /// <summary>Earliest work-order request date for this unit's current visible make-ready set.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Latest scheduled work-order date/window for open tasks, used as the target ready date.</summary>
    public DateTime? TargetReadyDate { get; set; }

    /// <summary>Latest work-order or receipt activity.</summary>
    public DateTime? LastActivityAt { get; set; }

    /// <summary>Days from first turnover task to now/done, computed after the SQL aggregate returns.</summary>
    public int? DaysInTurnover { get; set; }
}

/// <summary>Compact canonical relationship/agreement projection for the header / overview.</summary>
public class UnitLeaseSummary
{
    /// <summary>The immutable governing or upcoming LeaseAgreement id.</summary>
    public int Id { get; set; }

    /// <summary>The continuous household/account relationship containing this agreement.</summary>
    public int LeaseManagementId { get; set; }

    public int? TenantAccountId { get; set; }
    public string LeaseNumber { get; set; } = string.Empty;

    /// <summary>Database-derived agreement status (for example Governing or Upcoming).</summary>
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
    /// <summary>The same immutable TenantLedgerEntry id used by global account history.</summary>
    public long Id { get; set; }
    public int TenantAccountId { get; set; }
    public int LeaseManagementId { get; set; }
    public int? LeaseAgreementId { get; set; }

    /// <summary>Payment type as its string name (e.g. <c>Rent</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Payment status as its string name (e.g. <c>Paid</c>).</summary>
    public string Status { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
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

    /// <summary>The kind of record the file is attached to (e.g. <c>LeaseAgreement</c>, <c>WorkOrder</c>).</summary>
    public string? EntityType { get; set; }
    public long? EntityId { get; set; }
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
