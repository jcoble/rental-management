namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Handles an inbound SMS from a vendor replying DONE/COMPLETE/FINISHED to a dispatched job. Matches
/// the sender's phone to a vendor with an OPEN <c>VendorDispatch</c>, marks that dispatch + its work
/// order Completed, and notifies the landlord. Deterministic keyword matching only (no LLM).
/// </summary>
public interface ISmsInboundVendorDoneService
{
    /// <summary>
    /// True when the sender is a vendor with an open dispatch AND the body is a DONE keyword — i.e.
    /// this inbound message is a vendor completion the router should hand to <see cref="HandleAsync"/>
    /// rather than the rent-confirmation path.
    /// </summary>
    Task<bool> CanHandleAsync(string? fromPhone, string? body, CancellationToken ct = default);

    Task<SmsInboundVendorDoneResult> HandleAsync(
        string? fromPhone,
        string? body,
        DateTime receivedAtUtc,
        CancellationToken ct = default);
}

public sealed record SmsInboundVendorDoneResult(
    bool Handled,
    int? WorkOrderId,
    string ResponseMessage);
