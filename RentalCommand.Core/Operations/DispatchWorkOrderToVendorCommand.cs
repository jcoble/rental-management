using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Operations;

public sealed record DispatchWorkOrderToVendorCommand(
    int PortfolioId,
    int WorkOrderId,
    int VendorId,
    string DestinationPhone,
    string Message,
    int? ChangedByUserId,
    [property: AtomicFingerprintIgnore] DateTime DispatchedAtUtc,
    DispatchManagementAccess? ManagementAccess = null) : IAtomicCommandData;

public sealed record DispatchManagementAccess(
    Guid SessionId,
    int UserId,
    int AccessContextId,
    long AccessRevision) : IAtomicCommandData;

public enum DispatchWorkOrderToVendorOutcome
{
    Dispatched,
    NotFound,
    AlreadyDispatched,
}

public sealed record DispatchWorkOrderToVendorResult(
    DispatchWorkOrderToVendorOutcome Outcome,
    int DispatchId,
    int PortfolioId,
    int WorkOrderId,
    int VendorId,
    VendorDispatchStatus Status,
    DateTime DispatchedAtUtc,
    string? Message);

public sealed record CancelVendorDispatchCommand(
    int PortfolioId,
    int WorkOrderId,
    int DispatchId,
    int? ChangedByUserId,
    string Reason,
    [property: AtomicFingerprintIgnore] DateTime CancelledAtUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey,
    DispatchManagementAccess? ManagementAccess = null) : IAtomicCommandData;

public enum CancelVendorDispatchOutcome
{
    Cancelled,
    NotFound,
    AlreadyClosed,
}

public sealed record CancelVendorDispatchResult(
    CancelVendorDispatchOutcome Outcome,
    int DispatchId,
    int PortfolioId,
    int WorkOrderId,
    int VendorId,
    VendorDispatchStatus Status,
    DateTime CancelledAtUtc,
    string? Reason);
