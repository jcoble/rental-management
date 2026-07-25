namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Handles an inbound SMS from a vendor replying DONE/COMPLETE/FINISHED to a dispatched job. Matches
/// the sender's phone to a vendor with an OPEN <c>VendorDispatch</c>, marks that dispatch + its work
/// order Completed, and notifies the landlord. Deterministic keyword matching only (no LLM).
/// </summary>
public interface ISmsInboundVendorDoneService
{
    Task<SmsInboundVendorDoneResult> TryHandleAsync(
        string providerEventId,
        string? fromPhone,
        string? body,
        DateTime receivedAtUtc,
        CancellationToken ct = default);
}

public sealed record SmsInboundVendorDoneResult(
    bool Handled,
    int? WorkOrderId,
    string ResponseMessage);
