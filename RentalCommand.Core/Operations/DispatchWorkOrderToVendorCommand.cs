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
    DateTime DispatchedAtUtc) : IAtomicCommandData;

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
    string? Message) : IAtomicResultData;
