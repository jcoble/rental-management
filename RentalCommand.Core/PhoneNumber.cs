namespace RentalCommand.Core;

/// <summary>Canonical SMS phone representation shared by persisted facts and provider boundaries.</summary>
public static class PhoneNumber
{
    public static string? Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 10) return "+1" + digits;
        if (digits.Length == 11 && digits.StartsWith('1')) return "+" + digits;
        return digits.Length > 0 ? "+" + digits : null;
    }
}
