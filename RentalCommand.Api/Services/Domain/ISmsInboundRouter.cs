namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Routes a verified inbound SMS to the vendor-DONE handler. A sender must uniquely match one open
/// dispatch; every other event is a no-op. Inbound SMS never confirms rent or mutates payments.
/// </summary>
public interface ISmsInboundRouter
{
    Task<string> RouteAsync(
        string providerEventId,
        string? fromPhone,
        string? body,
        DateTime receivedAtUtc,
        CancellationToken ct = default);
}
