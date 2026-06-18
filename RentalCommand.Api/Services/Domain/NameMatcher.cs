using System.Text;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// The shared fuzzy name-match engine, extracted from <see cref="BankingService"/> so the
/// banking bank-feed matcher and the accounting import mapper use ONE implementation rather than
/// two copies. Pure string functions: normalize, tokenize (dropping generic filler), and score the
/// strength in [0,1] that an external label refers to one of a set of candidate names.
///
/// <para>
/// "Match an external string to an internal entity by name" is the same problem whether the
/// external string is a bank-line merchant or a QuickBooks Customer/Vendor/Class — both reuse
/// <see cref="NameMatchStrength"/>. The date+amount <c>CombineScore</c> that bank reconciliation
/// layers on top stays in <see cref="BankingService"/> (it is transaction-specific).
/// </para>
/// </summary>
public static class NameMatcher
{
    // Tokens with at least 2 characters that are not generic banking/transfer/company filler, so noise
    // like "ach", "the", "llc" doesn't manufacture a false name match.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "ach", "the", "and", "llc", "inc", "co", "payment", "pmt", "deposit", "debit", "credit",
        "transfer", "xfer", "online", "pos", "purchase", "rent", "from", "for", "ref", "id",
    };

    /// <summary>
    /// Strength in [0,1] that <paramref name="externalText"/> refers to one of
    /// <paramref name="candidateNames"/>. 1.0 = a candidate name is fully contained in the external
    /// text (or vice-versa); partial credit for the fraction of a candidate's significant tokens found
    /// in the external text; 0 when there is nothing to compare or no overlap.
    /// </summary>
    public static decimal NameMatchStrength(string? externalText, params string?[] candidateNames)
    {
        var text = NormalizeName(externalText);
        if (text.Length == 0)
        {
            return 0m;
        }

        var textTokens = SignificantTokens(text);
        if (textTokens.Count == 0)
        {
            return 0m;
        }

        var best = 0m;
        foreach (var candidate in candidateNames)
        {
            var normalized = NormalizeName(candidate);
            if (normalized.Length == 0)
            {
                continue;
            }

            // Whole-name containment either way is the strongest signal.
            if (text.Contains(normalized, StringComparison.Ordinal)
                || normalized.Contains(text, StringComparison.Ordinal))
            {
                return 1m;
            }

            var candidateTokens = SignificantTokens(normalized);
            if (candidateTokens.Count == 0)
            {
                continue;
            }

            var shared = candidateTokens.Count(t => textTokens.Contains(t));
            if (shared == 0)
            {
                continue;
            }

            // Fraction of the candidate's significant tokens found in the external text.
            var fraction = (decimal)shared / candidateTokens.Count;
            if (fraction > best)
            {
                best = fraction;
            }
        }

        return best;
    }

    /// <summary>Lowercases, strips punctuation, and collapses whitespace so names compare cleanly.</summary>
    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                sb.Append(' ');
            }
            // drop punctuation entirely
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Significant tokens of a normalized string: length ≥ 2 and not a stop word.</summary>
    public static HashSet<string> SignificantTokens(string normalized) =>
        normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);
}
