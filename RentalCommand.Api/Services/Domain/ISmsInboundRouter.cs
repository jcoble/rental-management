namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Routes a verified inbound SMS to the right handler: a vendor replying DONE to a dispatched job
/// (when the sender is a vendor with an open dispatch and used a DONE keyword), otherwise the existing
/// tenant rent-YES confirmation. Returns the TwiML-ready reply text for whichever handler ran.
/// </summary>
public interface ISmsInboundRouter
{
    Task<string> RouteAsync(string? fromPhone, string? body, DateTime receivedAtUtc, CancellationToken ct = default);
}
