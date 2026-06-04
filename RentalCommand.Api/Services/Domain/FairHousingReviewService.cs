using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IFairHousingReviewService"/>
public class FairHousingReviewService : IFairHousingReviewService
{
    private const int MaxTextLength = 8000;
    private const int MaxIssues = 25;

    private readonly ILlmProvider _llm;
    private readonly ILogger<FairHousingReviewService> _logger;

    public FairHousingReviewService(ILlmProvider llm, ILogger<FairHousingReviewService> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    /// <summary>
    /// The "not actually checked" result. Used whenever the LLM is a no-op (no key) or returns
    /// unusable output. Crucially this is <c>Compliant = false</c> so we never imply a clean bill of
    /// health for copy that was never reviewed.
    /// </summary>
    private static FairHousingReviewResult Unavailable() => new()
    {
        Reviewed = false,
        Compliant = false,
        Issues = [],
        SuggestedRewrite = null,
    };

    public async Task<FairHousingReviewResult> ReviewAsync(string text, CancellationToken ct = default)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return Unavailable();
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];

        var prompt = BuildPrompt(text);

        string raw;
        try
        {
            raw = (await _llm.ChatAsync(prompt, ct))?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            // A provider error is treated exactly like "unavailable" — never a false "compliant".
            _logger.LogWarning(ex, "Fair Housing review LLM call failed; reporting review unavailable.");
            return Unavailable();
        }

        // Empty = no-op provider (no API key configured) → review unavailable.
        if (string.IsNullOrWhiteSpace(raw)) return Unavailable();

        if (!TryParseResult(raw, out var result))
        {
            _logger.LogWarning("Fair Housing review returned unparseable output; reporting review unavailable.");
            return Unavailable();
        }

        return result;
    }

    private static string BuildPrompt(string text)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a Fair Housing Act compliance reviewer for a residential landlord.");
        sb.AppendLine("Review the copy below (a tenant notice or property listing) for any language that");
        sb.AppendLine("references — or could reasonably be read to discriminate by, steer toward, or state a");
        sb.AppendLine("preference about — any federally protected class:");
        sb.AppendLine("- Race or color");
        sb.AppendLine("- Religion");
        sb.AppendLine("- Sex or gender");
        sb.AppendLine("- National origin");
        sb.AppendLine("- Familial status (children/kids, families, pregnancy)");
        sb.AppendLine("- Disability (physical or mental)");
        sb.AppendLine();
        sb.AppendLine("Also flag steering or preference phrasing even when no protected class is named explicitly,");
        sb.AppendLine("for example: \"no kids\", \"adults only\", \"perfect for a single professional\", \"ideal for a");
        sb.AppendLine("Christian family\", \"must be able-bodied\", \"English speakers only\", \"great for a bachelor\".");
        sb.AppendLine("Do NOT flag legitimate, lawful terms (rent amount, due dates, occupancy limits stated as a");
        sb.AppendLine("number of persons per local code, accessibility features described factually, etc.).");
        sb.AppendLine();
        sb.AppendLine("Copy to review (between the lines):");
        sb.AppendLine("----------");
        sb.AppendLine(text);
        sb.AppendLine("----------");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a strict JSON object, no markdown and no extra commentary, of the form:");
        sb.AppendLine("{");
        sb.AppendLine("  \"compliant\": true | false,");
        sb.AppendLine("  \"issues\": [ { \"phrase\": \"<the exact risky phrase from the copy>\", \"concern\": \"<plain-language reason>\" } ],");
        sb.AppendLine("  \"suggestedRewrite\": \"<a compliant rewrite of the FULL copy, or null if already compliant>\"");
        sb.AppendLine("}");
        sb.AppendLine("If the copy is fully compliant, set compliant=true, issues=[], suggestedRewrite=null.");
        sb.AppendLine("If you flag any issue, set compliant=false and provide a complete compliant rewrite.");
        return sb.ToString();
    }

    /// <summary>
    /// Parses the model's strict-JSON verdict, tolerating ```json fences and surrounding prose. Returns
    /// false only when no usable JSON object could be found — callers treat that as "review unavailable".
    /// </summary>
    private static bool TryParseResult(string raw, out FairHousingReviewResult result)
    {
        result = Unavailable();

        var text = raw.Trim();

        // Strip a ```json … ``` fence if the model added one.
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }

        // Pull out the first {...} block if there's surrounding prose.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        text = text[start..(end + 1)];

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var issues = new List<FairHousingIssue>();
            if (root.TryGetProperty("issues", out var issuesEl) && issuesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in issuesEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var phrase = item.TryGetProperty("phrase", out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString()?.Trim() ?? string.Empty
                        : string.Empty;
                    var concern = item.TryGetProperty("concern", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString()?.Trim() ?? string.Empty
                        : string.Empty;
                    if (phrase.Length == 0 && concern.Length == 0) continue;
                    issues.Add(new FairHousingIssue { Phrase = phrase, Concern = concern });
                    if (issues.Count >= MaxIssues) break;
                }
            }

            // Trust an explicit compliant flag when present; otherwise infer from the issue list so a
            // model that omits the flag can't accidentally read as "compliant".
            bool compliant;
            if (root.TryGetProperty("compliant", out var comp) &&
                (comp.ValueKind == JsonValueKind.True || comp.ValueKind == JsonValueKind.False))
            {
                compliant = comp.GetBoolean();
            }
            else
            {
                compliant = issues.Count == 0;
            }

            // If issues exist, the copy is not compliant regardless of the model's flag.
            if (issues.Count > 0) compliant = false;

            string? rewrite = null;
            if (root.TryGetProperty("suggestedRewrite", out var rw) && rw.ValueKind == JsonValueKind.String)
            {
                var s = rw.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s)) rewrite = s;
            }

            result = new FairHousingReviewResult
            {
                Reviewed = true,
                Compliant = compliant,
                Issues = issues,
                SuggestedRewrite = compliant ? null : rewrite,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
