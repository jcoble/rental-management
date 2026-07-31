using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Operations;

public sealed record StaffOperationActor(
    int UserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long AccessRevision) : IAtomicCommandData;

public sealed record CreateWorkOrderCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int PropertyId,
    int? UnitId,
    int? TenantId,
    int? LeaseManagementId,
    int? VendorId,
    string Title,
    string Description,
    string? TechnicianAccessInstructions,
    string? SubmittedByLabel,
    string? RequesterName,
    string? RequesterPhone,
    string? RequesterEmail,
    bool? ResidentMustBePresent,
    bool? CallBeforeEntry,
    bool? CallIfNotHome,
    bool? PermissionToEnter,
    string? EntryNotes,
    string? PetWarnings,
    string? AccessWarnings,
    string Category,
    WorkOrderPriority Priority,
    WorkOrderStatus Status,
    DateTime? RequestedAtUtc,
    DateTimeOffset? ScheduledForLocal,
    DateTimeOffset? ScheduledWindowEndLocal,
    DateTime? ScheduledForUtc,
    DateTime? ScheduledWindowEndUtc,
    DateTime? CompletedAtUtc,
    decimal? EstimatedCost,
    decimal? ActualCost,
    string? CreatedBy,
    string? ExtractedData,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateWorkOrderCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int WorkOrderId,
    int? UnitId,
    bool ClearUnit,
    int? TenantId,
    bool ClearTenant,
    int? LeaseManagementId,
    bool ClearLeaseManagement,
    int? VendorId,
    string? Title,
    string? Description,
    string? TechnicianAccessInstructions,
    string? SubmittedByLabel,
    string? RequesterName,
    string? RequesterPhone,
    string? RequesterEmail,
    bool? ResidentMustBePresent,
    bool? CallBeforeEntry,
    bool? CallIfNotHome,
    bool? PermissionToEnter,
    string? EntryNotes,
    string? PetWarnings,
    string? AccessWarnings,
    string? Category,
    WorkOrderPriority? Priority,
    WorkOrderStatus? Status,
    string? StatusNote,
    DateTime? RequestedAtUtc,
    DateTime? ScheduledForUtc,
    DateTime? ScheduledWindowEndUtc,
    DateTime? CompletedAtUtc,
    decimal? EstimatedCost,
    decimal? ActualCost,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record DeleteWorkOrderCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int WorkOrderId,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record CreateTenantWorkOrderCommand(
    int PortfolioId,
    int TenantUserId,
    Guid TenantAuthSessionId,
    int TenantAccessContextId,
    long TenantAccessRevision,
    string Title,
    string Description,
    string Category,
    WorkOrderPriority Priority,
    [property: AtomicFingerprintIgnore] DateTime RequestedAtUtc,
    string? ContactPhone,
    string? ContactEmail,
    bool? ResidentMustBePresent,
    bool? CallBeforeEntry,
    bool? CallIfNotHome,
    bool? PermissionToEnter,
    string? EntryNotes,
    string? PetWarnings,
    string? AccessWarnings,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AddStaffWorkOrderCommentCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int WorkOrderId,
    string Body,
    bool IsPrivate,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AddTenantWorkOrderCommentCommand(
    int PortfolioId,
    int TenantUserId,
    Guid TenantAuthSessionId,
    int TenantAccessContextId,
    long TenantAccessRevision,
    int WorkOrderId,
    string Body,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateTenantWorkOrderCommand(
    int PortfolioId,
    int TenantUserId,
    Guid TenantAuthSessionId,
    int TenantAccessContextId,
    long TenantAccessRevision,
    int WorkOrderId,
    string? Title,
    string? Description,
    string? RequesterName,
    string? RequesterPhone,
    string? RequesterEmail,
    bool? ResidentMustBePresent,
    bool? CallBeforeEntry,
    bool? CallIfNotHome,
    bool? PermissionToEnter,
    string? EntryNotes,
    string? PetWarnings,
    string? AccessWarnings,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record CancelTenantWorkOrderCommand(
    int PortfolioId,
    int TenantUserId,
    Guid TenantAuthSessionId,
    int TenantAccessContextId,
    long TenantAccessRevision,
    int WorkOrderId,
    string? Note,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum OperationMutationOutcome { Applied, NotFound }

public sealed record OperationMutationResult(
    OperationMutationOutcome Outcome,
    int EntityId,
    string? ResponseJson = null);

public sealed record WorkOrderMutationResult(
    OperationMutationOutcome Outcome,
    int EntityId,
    WorkOrderMutationSnapshot? Snapshot = null,
    WorkOrderMutationActivityReceipt? Receipt = null);

public sealed record WorkOrderMutationSnapshot(
    int Id,
    int PortfolioId,
    int PropertyId,
    int? UnitId,
    int? TenantId,
    int? LeaseManagementId,
    int? VendorId,
    int? RecurringMaintenanceTaskId,
    string Title,
    string Description,
    string? TechnicianAccessInstructions,
    string? SubmittedByLabel,
    string? RequesterName,
    string? RequesterPhone,
    string? RequesterEmail,
    bool? ResidentMustBePresent,
    bool? CallBeforeEntry,
    bool? CallIfNotHome,
    bool? PermissionToEnter,
    string? EntryNotes,
    string? PetWarnings,
    string? AccessWarnings,
    string Category,
    WorkOrderPriority Priority,
    WorkOrderStatus Status,
    DateTime RequestedAt,
    DateTime? ScheduledFor,
    DateTime? ScheduledWindowEnd,
    DateTime? CompletedAt,
    decimal? EstimatedCost,
    decimal? ActualCost,
    string? CreatedBy,
    DateTime UpdatedAt,
    string? PropertyName,
    string? UnitNumber,
    string? VendorName,
    string? TenantName);

public sealed record WorkOrderMutationActivityReceipt(
    int EntityId,
    OperationMutationOutcome Outcome,
    int? ActivityId,
    DateTime CommittedAtUtc);
