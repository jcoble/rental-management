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
        var parts = new List<string>();

        foreach (var part in new[] { line1, line2, city, stateZip })
        {
            if (string.IsNullOrWhiteSpace(part))
                continue;

            var trimmed = part.Trim();
            if (IsCoveredByExistingPart(parts, trimmed))
                continue;

            parts.Add(trimmed);
        }

        var composed = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(composed) ? null : composed;
    }

    private static bool IsCoveredByExistingPart(IReadOnlyList<string> existingParts, string candidate)
    {
        var normalizedCandidate = NormalizeForContainment(candidate);
        if (normalizedCandidate.Length == 0)
            return true;

        return existingParts.Any(existing =>
        {
            var normalizedExisting = NormalizeForContainment(existing);
            return ContainsTokenSequence(normalizedExisting, normalizedCandidate) ||
                   ContainsTokenSequence(normalizedCandidate, normalizedExisting);
        });
    }

    private static bool ContainsTokenSequence(string value, string candidate)
    {
        if (value.Length == 0 || candidate.Length == 0)
            return false;

        return $" {value} ".Contains($" {candidate} ", StringComparison.Ordinal);
    }

    private static string NormalizeForContainment(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();

        return string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
