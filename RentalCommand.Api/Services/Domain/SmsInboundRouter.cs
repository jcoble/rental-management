namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISmsInboundRouter"/>
public sealed class SmsInboundRouter : ISmsInboundRouter
{
    private readonly ISmsInboundVendorDoneService _vendorDone;

    public SmsInboundRouter(ISmsInboundVendorDoneService vendorDone) => _vendorDone = vendorDone;

    public async Task<string> RouteAsync(
        string providerEventId,
        string? fromPhone,
        string? body,
        DateTime receivedAtUtc,
        CancellationToken ct = default)
    {
        // TSK-672 clean replacement: verified inbound SMS can only complete one uniquely matched
        // vendor dispatch. The former rent-confirmation fallback was obsolete and is intentionally
        // absent; an unmatched/ambiguous DONE is durably receipted as a no-op by the atomic handler.
        var vendorResult = await _vendorDone.TryHandleAsync(
            providerEventId, fromPhone, body, receivedAtUtc, ct);
        return vendorResult.ResponseMessage;
    }
}
