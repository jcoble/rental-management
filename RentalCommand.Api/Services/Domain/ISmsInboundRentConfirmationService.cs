namespace RentalCommand.Api.Services.Domain;

public interface ISmsInboundRentConfirmationService
{
    Task<SmsInboundRentConfirmationResult> HandleAsync(
        string? fromPhone,
        string? body,
        DateTime receivedAtUtc,
        CancellationToken ct = default);
}

public sealed record SmsInboundRentConfirmationResult(
    bool Handled,
    int? PaymentId,
    string ResponseMessage);
