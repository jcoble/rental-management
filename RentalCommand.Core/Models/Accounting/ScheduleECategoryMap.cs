using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Models.Accounting;

/// <summary>
/// The bidirectional seed map between IRS Schedule-E categories and the conventional
/// accounting account names (plan §3.3). Used on PULL to auto-classify an imported expense's
/// account into a category, and on PUSH to pick the account name for an outgoing expense.
///
/// <para>
/// Auto-mapping by name only (D-4): an unmatched account falls back to
/// <see cref="ScheduleECategory.Other"/> with a review hint; the landlord can override per company
/// in a (Phase 4) one-time confirm. Matching is case/space/punctuation-insensitive and also accepts
/// the obvious shorthand a chart of accounts tends to use ("Repairs &amp; Maintenance" → Repairs).
/// </para>
/// </summary>
public static class ScheduleECategoryMap
{
    /// <summary>Canonical QBO-style account name for each category (the PUSH direction + display).</summary>
    private static readonly IReadOnlyDictionary<ScheduleECategory, string> CanonicalNames =
        new Dictionary<ScheduleECategory, string>
        {
            [ScheduleECategory.Advertising] = "Advertising",
            [ScheduleECategory.AutoTravel] = "Auto and Travel",
            [ScheduleECategory.CleaningMaintenance] = "Cleaning and Maintenance",
            [ScheduleECategory.Commissions] = "Commissions",
            [ScheduleECategory.Insurance] = "Insurance",
            [ScheduleECategory.LegalProfessional] = "Legal and Professional Fees",
            [ScheduleECategory.ManagementFees] = "Management Fees",
            [ScheduleECategory.MortgageInterest] = "Mortgage Interest",
            [ScheduleECategory.Repairs] = "Repairs",
            [ScheduleECategory.Supplies] = "Supplies",
            [ScheduleECategory.Taxes] = "Taxes",
            [ScheduleECategory.Utilities] = "Utilities",
            [ScheduleECategory.Depreciation] = "Depreciation",
            [ScheduleECategory.Other] = "Other Rental Expenses",
        };

    /// <summary>
    /// Normalized account-name fragments → category, for the PULL direction. The matcher checks whether
    /// any of a category's tokens is contained in the (normalized) account name, longest-key-first so a
    /// specific phrase wins over a generic word.
    /// </summary>
    private static readonly IReadOnlyList<(string Token, ScheduleECategory Category)> PullTokens = BuildPullTokens();

    /// <summary>The canonical account name for a category (PUSH side).</summary>
    public static string ToAccountName(ScheduleECategory category)
        => CanonicalNames.TryGetValue(category, out var name) ? name : "Other Rental Expenses";

    /// <summary>
    /// Best Schedule-E category for an external account name (PULL side). Falls back to
    /// <see cref="ScheduleECategory.Other"/> when nothing matches.
    /// </summary>
    public static ScheduleECategory FromAccountName(string? accountName)
    {
        var normalized = Normalize(accountName);
        if (normalized.Length == 0)
        {
            return ScheduleECategory.Other;
        }

        foreach (var (token, category) in PullTokens)
        {
            if (normalized.Contains(token, StringComparison.Ordinal))
            {
                return category;
            }
        }

        return ScheduleECategory.Other;
    }

    private static List<(string, ScheduleECategory)> BuildPullTokens()
    {
        // Distinctive tokens/phrases per category. Order by descending length so "mortgage interest"
        // beats "interest", "management fees" beats "fees", etc.
        var raw = new List<(string, ScheduleECategory)>
        {
            ("advertising", ScheduleECategory.Advertising),
            ("auto and travel", ScheduleECategory.AutoTravel),
            ("travel", ScheduleECategory.AutoTravel),
            ("mileage", ScheduleECategory.AutoTravel),
            ("cleaning and maintenance", ScheduleECategory.CleaningMaintenance),
            ("cleaning", ScheduleECategory.CleaningMaintenance),
            ("janitorial", ScheduleECategory.CleaningMaintenance),
            ("commissions", ScheduleECategory.Commissions),
            ("commission", ScheduleECategory.Commissions),
            ("insurance", ScheduleECategory.Insurance),
            ("legal and professional fees", ScheduleECategory.LegalProfessional),
            ("legal", ScheduleECategory.LegalProfessional),
            ("professional", ScheduleECategory.LegalProfessional),
            ("accounting", ScheduleECategory.LegalProfessional),
            ("management fees", ScheduleECategory.ManagementFees),
            ("management", ScheduleECategory.ManagementFees),
            ("mortgage interest", ScheduleECategory.MortgageInterest),
            ("mortgage", ScheduleECategory.MortgageInterest),
            ("interest", ScheduleECategory.MortgageInterest),
            ("repairs and maintenance", ScheduleECategory.Repairs),
            ("repairs", ScheduleECategory.Repairs),
            ("repair", ScheduleECategory.Repairs),
            ("maintenance", ScheduleECategory.Repairs),
            ("supplies", ScheduleECategory.Supplies),
            ("taxes", ScheduleECategory.Taxes),
            ("property tax", ScheduleECategory.Taxes),
            ("utilities", ScheduleECategory.Utilities),
            ("utility", ScheduleECategory.Utilities),
            ("electric", ScheduleECategory.Utilities),
            ("water", ScheduleECategory.Utilities),
            ("gas", ScheduleECategory.Utilities),
            ("depreciation", ScheduleECategory.Depreciation),
        };

        return raw
            .Select(x => (Token: Normalize(x.Item1), x.Item2))
            .Where(x => x.Token.Length > 0)
            .OrderByDescending(x => x.Token.Length)
            .ToList();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ');
        var collapsed = string.Join(' ', new string(chars.ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed;
    }
}
