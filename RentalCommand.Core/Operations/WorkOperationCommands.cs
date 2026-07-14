using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Operations;

public sealed record StaffOperationActor(
    int UserId,
    Guid AuthSessionId,
    int AccessContextId,
    long AccessRevision) : IAtomicCommandData;

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
    string DeliveryIdempotencyKey) : IAtomicCommandData;

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
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record DeleteWorkOrderCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int WorkOrderId,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

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
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum OperationMutationOutcome { Applied, NotFound }

public sealed record OperationMutationResult(
    OperationMutationOutcome Outcome,
    int EntityId,
    string? ResponseJson = null) : IAtomicResultData;
