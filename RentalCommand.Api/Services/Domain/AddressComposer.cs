namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Composes a structured address (line1/line2/city/state/zip) into a single display line,
/// e.g. "123 Main St, Apt 4, Columbus, OH 43004". Used to keep the legacy single-line address
/// columns (OwnerEntity.Address, RentalApplication.CurrentAddress) in sync on write so older read
/// paths keep working while the forms move to structured fields.
/// </summary>
public static class AddressComposer
{
    /// <summary>Returns the composed line, or null when no structured parts are present.</summary>
    public static string? Compose(string? line1, string? line2, string? city, string? state, string? postalCode)
    {
        var stateZip = string.Join(' ', new[] { state, postalCode }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var parts = new[] { line1, line2, city, stateZip }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim());
        var composed = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(composed) ? null : composed;
    }
}
