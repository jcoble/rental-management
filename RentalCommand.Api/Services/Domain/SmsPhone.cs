namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Shared SMS phone-number normalization. Reduces a free-form phone string to a canonical
/// <c>+E.164</c>-ish form (US default) so an inbound caller-id can be matched against stored numbers
/// regardless of formatting. Shared by verified inbound vendor-dispatch matching.
/// </summary>
internal static class SmsPhone
{
    public static string Normalize(string? phone) => RentalCommand.Core.PhoneNumber.Normalize(phone) ?? string.Empty;
}
