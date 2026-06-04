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

    public async Task<string> RouteAsync(string? fromPhone, string? body, DateTime receivedAtUtc, CancellationToken ct = default)
    {
        // Vendor DONE wins when the sender is a vendor with an open dispatch and used a DONE keyword.
        // This is checked first so a vendor's "DONE" never gets read as a tenant rent confirmation.
        if (await _vendorDone.CanHandleAsync(fromPhone, body, ct))
        {
            var vendorResult = await _vendorDone.HandleAsync(fromPhone, body, receivedAtUtc, ct);
            return vendorResult.ResponseMessage;
        }

        var rentResult = await _rentConfirmation.HandleAsync(fromPhone, body, receivedAtUtc, ct);
        return rentResult.ResponseMessage;
    }
}
