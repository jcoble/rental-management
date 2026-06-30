using System.Text.RegularExpressions;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Pure, deterministic merge-token fill for notice templates. Replaces every <c>{{token}}</c> with the
/// matching value from <paramref name="tokens"/> (case-insensitive, space-tolerant). Unknown or unmapped
/// tokens render to an empty string — never throws, so a template can never break generation.
/// </summary>
public static partial class NoticeTemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex TokenRegex();

    public static (string Subject, string Body) Render(
        string subjectTemplate,
        string bodyTemplate,
        IReadOnlyDictionary<string, string> tokens)
    {
        return (Fill(subjectTemplate, tokens), Fill(bodyTemplate, tokens));
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        return TokenRegex().Replace(template, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            return tokens.TryGetValue(key, out var value) ? value : string.Empty;
        });
    }
}
