namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISmsInboundRouter"/>
public sealed class SmsInboundRouter : ISmsInboundRouter
{
    private readonly ISmsInboundVendorDoneService _vendorDone;
    private readonly ISmsInboundRentConfirmationService _rentConfirmation;

    public SmsInboundRouter(
        ISmsInboundVendorDoneService vendorDone,
        ISmsInboundRentConfirmationService rentConfirmation)
    {
        _vendorDone = vendorDone;
        _rentConfirmation = rentConfirmation;
    }

    public async Task<string> RouteAsync(
        string providerEventId,
        string? fromPhone,
        string? body,
        DateTime receivedAtUtc,
        CancellationToken ct = default)
    {
        // Vendor DONE wins when an open dispatch matches. The atomic handler performs the only match
        // query and returns NoOpenDispatch when the rent-confirmation path should be attempted.
        var vendorResult = await _vendorDone.TryHandleAsync(
            providerEventId, fromPhone, body, receivedAtUtc, ct);
        if (vendorResult.Handled)
        {
            return vendorResult.ResponseMessage;
        }

        var rentResult = await _rentConfirmation.HandleAsync(fromPhone, body, receivedAtUtc, ct);
        return rentResult.ResponseMessage;
    }
}
