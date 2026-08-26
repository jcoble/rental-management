using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="WorkOrder"/>.</summary>
public class WorkOrderResponse
{
    public int Id { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PortfolioId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PropertyId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UnitId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TenantId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LeaseManagementId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VendorId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RecurringMaintenanceTaskId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? TechnicianAccessInstructions { get; set; }
    public string? SubmittedByLabel { get; set; }
    public string? RequesterName { get; set; }
    public string? RequesterPhone { get; set; }
    public string? RequesterEmail { get; set; }
    public bool? ResidentMustBePresent { get; set; }
    public bool? CallBeforeEntry { get; set; }
    public bool? CallIfNotHome { get; set; }
    public bool? PermissionToEnter { get; set; }
    public string? EntryNotes { get; set; }
    public string? PetWarnings { get; set; }
    public string? AccessWarnings { get; set; }
    public string Category { get; set; } = "General";
    public WorkOrderPriority Priority { get; set; }
    public WorkOrderStatus Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ScheduledFor { get; set; }

    /// <summary>End of the scheduled arrival window (visit expected between <see cref="ScheduledFor"/> and this time). Null when none.</summary>
    public DateTime? ScheduledWindowEnd { get; set; }

    public DateTime? CompletedAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? EstimatedCost { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ActualCost { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Property name, projected from the <see cref="WorkOrder.Property"/> navigation. Null when not loaded.</summary>
    public string? PropertyName { get; set; }

    /// <summary>Unit number, projected from the <see cref="WorkOrder.Unit"/> navigation. Null when none/not loaded.</summary>
    public string? UnitNumber { get; set; }

    /// <summary>Vendor name, projected from the <see cref="WorkOrder.Vendor"/> navigation. Null when none/not loaded.</summary>
    public string? VendorName { get; set; }

    /// <summary>Tenant full name, projected from the <see cref="WorkOrder.Tenant"/> navigation. Null when none/not loaded.</summary>
    public string? TenantName { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>work-order-1</c>.</summary>
    public string TestId => $"work-order-{Id}";

    public static WorkOrderResponse FromEntity(WorkOrder e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        UnitId = e.UnitId,
        TenantId = e.TenantId,
        LeaseManagementId = e.LeaseManagementId,
        VendorId = e.VendorId,
        RecurringMaintenanceTaskId = e.RecurringMaintenanceTaskId,
        Title = e.Title,
        Description = e.Description,
        TechnicianAccessInstructions = e.TechnicianAccessInstructions,
        SubmittedByLabel = e.SubmittedByLabel,
        RequesterName = e.RequesterName,
        RequesterPhone = e.RequesterPhone,
        RequesterEmail = e.RequesterEmail,
        ResidentMustBePresent = e.ResidentMustBePresent,
        CallBeforeEntry = e.CallBeforeEntry,
        CallIfNotHome = e.CallIfNotHome,
        PermissionToEnter = e.PermissionToEnter,
        EntryNotes = e.EntryNotes,
        PetWarnings = e.PetWarnings,
        AccessWarnings = e.AccessWarnings,
        Category = e.Category,
        Priority = e.Priority,
        Status = e.Status,
        RequestedAt = e.RequestedAt,
        ScheduledFor = e.ScheduledFor,
        ScheduledWindowEnd = e.ScheduledWindowEnd,
        CompletedAt = e.CompletedAt,
        EstimatedCost = e.EstimatedCost,
        ActualCost = e.ActualCost,
        CreatedBy = e.CreatedBy,
        UpdatedAt = e.UpdatedAt,
        PropertyName = e.Property?.Name,
        UnitNumber = e.Unit?.UnitNumber,
        VendorName = e.Vendor?.Name,
        TenantName = e.Tenant == null ? null : $"{e.Tenant.FirstName} {e.Tenant.LastName}".Trim(),
    };
}

public class WorkOrderListResponse
{
    public IReadOnlyList<WorkOrderResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class WorkOrderListQuery : ListQuery
{
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? VendorId { get; set; }
    public bool OpenOnly { get; set; }
    public WorkOrderStatus? Status { get; set; }
    public WorkOrderPriority? Priority { get; set; }
    public DateTime? RequestedFrom { get; set; }
    public DateTime? RequestedTo { get; set; }
    public DateTime? ScheduledFrom { get; set; }
    public DateTime? ScheduledTo { get; set; }
    public DateTime? CompletedFrom { get; set; }
    public DateTime? CompletedTo { get; set; }
}

/// <summary>One entry in a work order's status timeline (oldest → newest in the parent list).</summary>
public class WorkOrderStatusEventResponse
{
    public int Id { get; set; }
    public string Kind { get; set; } = "Status";
    public string Visibility { get; set; } = "Public";

    /// <summary>Status moved away from; null for the initial create event.</summary>
    public WorkOrderStatus? FromStatus { get; set; }

    /// <summary>Status moved into.</summary>
    public WorkOrderStatus ToStatus { get; set; }

    public string? Note { get; set; }

    /// <summary>Human label for who/what made the change, e.g. "Staff", "Tenant", "System".</summary>
    public string? ChangedByLabel { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public static WorkOrderStatusEventResponse FromEntity(WorkOrderStatusEvent e) => new()
    {
        Id = e.Id,
        Kind = e.Kind,
        Visibility = e.Visibility,
        FromStatus = e.FromStatus,
        ToStatus = e.ToStatus,
        Note = e.Note,
        ChangedByLabel = e.ChangedByLabel,
        CreatedAtUtc = e.CreatedAtUtc,
    };
}

/// <summary>
/// Work-order detail: the full <see cref="WorkOrderResponse"/> plus the status <see cref="Timeline"/>
/// (oldest → newest). Returned only on the single-work-order GET; the list endpoint stays lightweight.
/// </summary>
public class WorkOrderDetailResponse : WorkOrderResponse
{
    public string DetailRole { get; set; } = "manager";
    public WorkOrderDetailCapabilities Capabilities { get; set; } = new();
    public IReadOnlyList<string> ResidentNames { get; set; } = [];
    public string? PrivateManagementNotes { get; set; }
    public IReadOnlyList<WorkOrderStatusEventResponse> Timeline { get; set; } = [];
    public IReadOnlyList<WorkOrderActivityResponse> Activity { get; set; } = [];

    /// <summary>True when a scanned source document is attached to this work order (drives the detail-page viewer).</summary>
    public bool HasScan { get; set; }

    /// <summary>True when the attached scan is an image (vs a PDF) — lets the UI show a thumbnail.</summary>
    public bool ScanIsImage { get; set; }

    /// <summary>
    /// True when this work order has an OPEN vendor dispatch (the vendor was texted the job and hasn't
    /// replied DONE yet). Drives the detail page's "vendor has the job … closes on DONE" banner so it
    /// reflects a real dispatch, not a mere vendor assignment. Computed DB-side; see the GET handler.
    /// </summary>
    public bool HasActiveDispatch { get; set; }

    /// <summary>The current open dispatch id, when <see cref="HasActiveDispatch"/> is true.</summary>
    public int? ActiveDispatchId { get; set; }

    public int? ActiveDispatchVendorId { get; set; }

    public string? ActiveDispatchVendorName { get; set; }

    public static WorkOrderDetailResponse FromEntity(WorkOrder e, IEnumerable<WorkOrderStatusEvent> events)
    {
        var detail = new WorkOrderDetailResponse
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            PropertyId = e.PropertyId,
            UnitId = e.UnitId,
            TenantId = e.TenantId,
            LeaseManagementId = e.LeaseManagementId,
            VendorId = e.VendorId,
            RecurringMaintenanceTaskId = e.RecurringMaintenanceTaskId,
            Title = e.Title,
            Description = e.Description,
            TechnicianAccessInstructions = e.TechnicianAccessInstructions,
            SubmittedByLabel = e.SubmittedByLabel,
            RequesterName = e.RequesterName,
            RequesterPhone = e.RequesterPhone,
            RequesterEmail = e.RequesterEmail,
            ResidentMustBePresent = e.ResidentMustBePresent,
            CallBeforeEntry = e.CallBeforeEntry,
            CallIfNotHome = e.CallIfNotHome,
            PermissionToEnter = e.PermissionToEnter,
            EntryNotes = e.EntryNotes,
            PetWarnings = e.PetWarnings,
            AccessWarnings = e.AccessWarnings,
            Category = e.Category,
            Priority = e.Priority,
            Status = e.Status,
            RequestedAt = e.RequestedAt,
            ScheduledFor = e.ScheduledFor,
            ScheduledWindowEnd = e.ScheduledWindowEnd,
            CompletedAt = e.CompletedAt,
            EstimatedCost = e.EstimatedCost,
            ActualCost = e.ActualCost,
            CreatedBy = e.CreatedBy,
            UpdatedAt = e.UpdatedAt,
            PropertyName = e.Property?.Name,
            UnitNumber = e.Unit?.UnitNumber,
            VendorName = e.Vendor?.Name,
            TenantName = e.Tenant == null ? null : $"{e.Tenant.FirstName} {e.Tenant.LastName}".Trim(),
            DetailRole = "manager",
            Capabilities = WorkOrderDetailCapabilities.Manager(e.Status),
            Timeline = events.Select(WorkOrderStatusEventResponse.FromEntity).ToList(),
        };
        return detail;
    }
}

public sealed class WorkOrderActivityResponse
{
    public int Id { get; set; }
    public string Kind { get; set; } = "Status";
    public WorkOrderStatus? FromStatus { get; set; }
    public WorkOrderStatus ToStatus { get; set; }
    public string? Note { get; set; }
    public string ActorLabel { get; set; } = "System";
    public string Visibility { get; set; } = "Public";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class WorkOrderMutationReceipt
{
    public int EntityId { get; set; }
    public string Outcome { get; set; } = "Applied";
    public int? ActivityId { get; set; }
    public DateTime CommittedAtUtc { get; set; }
}

public sealed class WorkOrderCommentRequest
{
    [Required]
    [MaxLength(2000)]
    public string Body { get; set; } = string.Empty;

    public bool IsPrivate { get; set; }
}

public sealed class TenantWorkOrderUpdateRequest
{
    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(4000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? RequesterName { get; set; }

    [MaxLength(64)]
    public string? RequesterPhone { get; set; }

    [MaxLength(320)]
    public string? RequesterEmail { get; set; }

    public bool? ResidentMustBePresent { get; set; }
    public bool? CallBeforeEntry { get; set; }
    public bool? CallIfNotHome { get; set; }
    public bool? PermissionToEnter { get; set; }

    [MaxLength(2000)]
    public string? EntryNotes { get; set; }

    [MaxLength(2000)]
    public string? PetWarnings { get; set; }

    [MaxLength(2000)]
    public string? AccessWarnings { get; set; }
}

public sealed class TenantWorkOrderCancelRequest
{
    [MaxLength(2000)]
    public string? Note { get; set; }
}

public sealed class WorkOrderDetailCapabilities
{
    public bool CanViewTenantContact { get; set; }
    public bool CanViewResidents { get; set; }
    public bool CanViewAccessInstructions { get; set; }
    public bool CanViewPrivateManagementNotes { get; set; }
    public bool CanViewCosts { get; set; }
    public bool CanCommentPublicly { get; set; }
    public bool CanCommentPrivately { get; set; }
    public bool CanUploadPhoto { get; set; }
    public bool CanDeletePhoto { get; set; }
    public bool CanCancel { get; set; }
    public bool CanEditRequestFields { get; set; }
    public bool CanEditManagementFields { get; set; }
    public bool CanAssignTechnician { get; set; }
    public bool CanDispatchVendor { get; set; }
    public IReadOnlyList<WorkOrderStatus> AllowedStatusTransitions { get; set; } = [];

    public static WorkOrderDetailCapabilities Tenant(WorkOrderStatus status) => new()
    {
        CanViewTenantContact = true,
        CanViewAccessInstructions = true,
        CanCommentPublicly = true,
        CanUploadPhoto = true,
        CanDeletePhoto = false,
        CanCancel = TenantCanCancel(status),
        CanEditRequestFields = TenantCanEdit(status),
        AllowedStatusTransitions = TenantCanCancel(status) ? [WorkOrderStatus.Cancelled] : [],
    };

    public static WorkOrderDetailCapabilities Maintenance(WorkOrderStatus status) => new()
    {
        CanViewTenantContact = true,
        CanViewResidents = true,
        CanViewAccessInstructions = true,
        CanCommentPublicly = true,
        CanUploadPhoto = true,
        CanDeletePhoto = false,
        AllowedStatusTransitions = TechnicianTransitions(status),
    };

    public static WorkOrderDetailCapabilities Manager(WorkOrderStatus status) => new()
    {
        CanViewTenantContact = true,
        CanViewResidents = true,
        CanViewAccessInstructions = true,
        CanViewPrivateManagementNotes = true,
        CanViewCosts = true,
        CanCommentPublicly = true,
        CanCommentPrivately = true,
        CanUploadPhoto = true,
        CanDeletePhoto = true,
        CanCancel = true,
        CanEditRequestFields = true,
        CanEditManagementFields = true,
        CanAssignTechnician = true,
        CanDispatchVendor = true,
        AllowedStatusTransitions = ManagerTransitions(status),
    };

    private static bool IsOpen(WorkOrderStatus status) =>
        status != WorkOrderStatus.Completed &&
        status != WorkOrderStatus.Cancelled &&
        status != WorkOrderStatus.Archived;

    private static bool TenantCanEdit(WorkOrderStatus status) =>
        status is WorkOrderStatus.New or WorkOrderStatus.Scheduled;

    private static bool TenantCanCancel(WorkOrderStatus status) =>
        status is WorkOrderStatus.New or WorkOrderStatus.Scheduled;

    private static IReadOnlyList<WorkOrderStatus> TechnicianTransitions(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.New =>
        [
            WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold,
            WorkOrderStatus.Cancelled, WorkOrderStatus.Completed,
        ],
        WorkOrderStatus.Scheduled =>
        [
            WorkOrderStatus.InProgress, WorkOrderStatus.WaitingParts, WorkOrderStatus.OnHold,
            WorkOrderStatus.Cancelled, WorkOrderStatus.Completed,
        ],
        WorkOrderStatus.InProgress =>
        [
            WorkOrderStatus.WaitingParts, WorkOrderStatus.OnHold, WorkOrderStatus.Completed,
        ],
        WorkOrderStatus.WaitingParts =>
        [
            WorkOrderStatus.InProgress, WorkOrderStatus.OnHold, WorkOrderStatus.Completed,
        ],
        WorkOrderStatus.OnHold =>
        [
            WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress, WorkOrderStatus.WaitingParts,
            WorkOrderStatus.Cancelled, WorkOrderStatus.Completed,
        ],
        _ => [],
    };

    private static IReadOnlyList<WorkOrderStatus> ManagerTransitions(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.New =>
        [
            WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold,
            WorkOrderStatus.Cancelled, WorkOrderStatus.Completed,
        ],
        WorkOrderStatus.Scheduled =>
        [
            WorkOrderStatus.InProgress, WorkOrderStatus.WaitingParts, WorkOrderStatus.OnHold,
            WorkOrderStatus.Cancelled, WorkOrderStatus.Completed,
        ],
        WorkOrderStatus.InProgress =>
        [
            WorkOrderStatus.WaitingParts, WorkOrderStatus.OnHold, WorkOrderStatus.Completed,
        ],
        WorkOrderStatus.WaitingParts =>
        [
            WorkOrderStatus.InProgress, WorkOrderStatus.OnHold, WorkOrderStatus.Completed,
        ],
        WorkOrderStatus.OnHold =>
        [
            WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress, WorkOrderStatus.WaitingParts,
            WorkOrderStatus.Cancelled, WorkOrderStatus.Completed,
        ],
        _ => [],
    };
}

public class CreateWorkOrderRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? TenantId { get; set; }

    [Range(1, int.MaxValue)]
    public int? LeaseManagementId { get; set; }

    [Range(1, int.MaxValue)]
    public int? VendorId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Access details that are safe to show to the assigned maintenance technician.
    /// Do not place financial, lease, or unrelated resident information here.
    /// </summary>
    [MaxLength(2000)]
    public string? TechnicianAccessInstructions { get; set; }

    [MaxLength(120)]
    public string? SubmittedByLabel { get; set; }

    [MaxLength(200)]
    public string? RequesterName { get; set; }

    [MaxLength(64)]
    public string? RequesterPhone { get; set; }

    [MaxLength(320)]
    public string? RequesterEmail { get; set; }

    public bool? ResidentMustBePresent { get; set; }
    public bool? CallBeforeEntry { get; set; }
    public bool? CallIfNotHome { get; set; }
    public bool? PermissionToEnter { get; set; }

    [MaxLength(2000)]
    public string? EntryNotes { get; set; }

    [MaxLength(2000)]
    public string? PetWarnings { get; set; }

    [MaxLength(2000)]
    public string? AccessWarnings { get; set; }

    [MaxLength(120)]
    public string Category { get; set; } = "General";

    [EnumDataType(typeof(WorkOrderPriority))]
    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;

    [EnumDataType(typeof(WorkOrderStatus))]
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.New;

    public DateTime? RequestedAt { get; set; }

    /// <summary>
    /// Start of the scheduled arrival window. Clients send this as an ISO-8601 instant carrying the
    /// landlord's local UTC offset (e.g. <c>2026-06-20T14:00:00-04:00</c>); the offset is preserved so
    /// the value is stored as the true UTC instant AND the tenant SMS can be rendered back in the
    /// landlord's local time. A zoneless value is treated as already-UTC.
    /// </summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>End of the scheduled arrival window. Should be at/after <see cref="ScheduledFor"/>. Same offset-preserving convention as <see cref="ScheduledFor"/>.</summary>
    public DateTimeOffset? ScheduledWindowEnd { get; set; }

    public DateTime? CompletedAt { get; set; }

    [Range(0, 99999999)]
    public decimal? EstimatedCost { get; set; }

    [Range(0, 99999999)]
    public decimal? ActualCost { get; set; }

    [MaxLength(120)]
    public string? CreatedBy { get; set; }

    /// <summary>Full scan-extraction superset JSON (jsonb); populated when creating from a scan draft.</summary>
    public string? ExtractedData { get; set; }
}

public class UpdateWorkOrderRequest
{
    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    public bool ClearUnit { get; set; }

    [Range(1, int.MaxValue)]
    public int? TenantId { get; set; }

    public bool ClearTenant { get; set; }

    [Range(1, int.MaxValue)]
    public int? LeaseManagementId { get; set; }

    public bool ClearLeaseManagement { get; set; }

    public bool ClearEstimatedCost { get; set; }

    public bool ClearActualCost { get; set; }

    [Range(1, int.MaxValue)]
    public int? VendorId { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(4000)]
    public string? Description { get; set; }

    /// <summary>Replacement access details shown to the assigned technician.</summary>
    [MaxLength(2000)]
    public string? TechnicianAccessInstructions { get; set; }

    [MaxLength(120)]
    public string? SubmittedByLabel { get; set; }

    [MaxLength(200)]
    public string? RequesterName { get; set; }

    [MaxLength(64)]
    public string? RequesterPhone { get; set; }

    [MaxLength(320)]
    public string? RequesterEmail { get; set; }

    public bool? ResidentMustBePresent { get; set; }
    public bool? CallBeforeEntry { get; set; }
    public bool? CallIfNotHome { get; set; }
    public bool? PermissionToEnter { get; set; }

    [MaxLength(2000)]
    public string? EntryNotes { get; set; }

    [MaxLength(2000)]
    public string? PetWarnings { get; set; }

    [MaxLength(2000)]
    public string? AccessWarnings { get; set; }

    [MaxLength(120)]
    public string? Category { get; set; }

    [EnumDataType(typeof(WorkOrderPriority))]
    public WorkOrderPriority? Priority { get; set; }

    [EnumDataType(typeof(WorkOrderStatus))]
    public WorkOrderStatus? Status { get; set; }

    /// <summary>
    /// Optional free-text note recorded on the status timeline when this update changes
    /// <see cref="Status"/> (e.g. "parts ordered", "tenant let us in"). Ignored when the status
    /// is unchanged.
    /// </summary>
    [MaxLength(2000)]
    public string? StatusNote { get; set; }

    /// <summary>
    /// When the request was received. Editable on the detail page (Costs &amp; timing). Null = unchanged
    /// (mirrors the nullable-means-untouched PATCH semantics used for the other fields). Unlike
    /// <see cref="ScheduledFor"/>/<see cref="CompletedAt"/>, the entity column is non-nullable, so a
    /// value is required to change it and it can never be cleared back to null.
    /// </summary>
    public DateTime? RequestedAt { get; set; }

    /// <summary>
    /// Start of the scheduled arrival window. Null = unchanged (same nullable-means-untouched PATCH
    /// semantics). When supplied, clients send an ISO-8601 instant carrying the landlord's local UTC
    /// offset; the offset is preserved so the value stores as the true UTC instant and the tenant SMS
    /// renders in the landlord's local time. A zoneless value is treated as already-UTC.
    /// </summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>End of the scheduled arrival window. Null = unchanged (same nullable-means-untouched PATCH semantics). Same offset-preserving convention as <see cref="ScheduledFor"/>.</summary>
    public DateTimeOffset? ScheduledWindowEnd { get; set; }

    public DateTime? CompletedAt { get; set; }

    [Range(0, 99999999)]
    public decimal? EstimatedCost { get; set; }

    [Range(0, 99999999)]
    public decimal? ActualCost { get; set; }
}
