namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Shared SMS phone-number normalization. Reduces a free-form phone string to a canonical
/// <c>+E.164</c>-ish form (US default) so an inbound caller-id can be matched against stored numbers
/// regardless of formatting. Mirrors the logic in <see cref="SmsInboundRentConfirmationService"/>.
/// </summary>
internal static class SmsPhone
{
    public static string Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 10) return "+1" + digits;
        if (digits.Length == 11 && digits.StartsWith('1')) return "+" + digits;
        return digits.Length > 0 ? "+" + digits : string.Empty;
    }
}
