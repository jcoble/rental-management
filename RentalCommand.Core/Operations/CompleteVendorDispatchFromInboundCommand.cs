using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Operations;

public sealed record CompleteVendorDispatchFromInboundCommand(
    string ProviderEventId,
    string NormalizedFromPhone,
    bool IsCompletionRequest,
    DateTime ReceivedAtUtc) : IAtomicCommandData;

public enum CompleteVendorDispatchFromInboundOutcome
{
    Applied,
    NoOpenDispatch,
}

public sealed record CompleteVendorDispatchFromInboundResult(
    CompleteVendorDispatchFromInboundOutcome Outcome,
    int PortfolioId,
    int DispatchId,
    int WorkOrderId,
    int VendorId,
    IReadOnlyList<int> NotificationIds) : IAtomicResultData;
