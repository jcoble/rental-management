namespace RentalCommand.Api.Services.Voice;

/// <summary>
/// Required-field ("slot") rules for the conversational voice intake (the
/// "Tell me" flow). Given a draft's record type and the fields extracted so far,
/// decides which required fields are still missing and what to ask next.
///
/// The server owns these rules so the client stays a thin renderer: it just
/// speaks <see cref="SlotEvaluation.NextPrompt"/> and stops when
/// <see cref="SlotEvaluation.Complete"/> is true.
///
/// Record types not listed in <see cref="RequiredByType"/> have no required
/// slots, so a draft of that type is always "complete" — this preserves the
/// prior single-shot voice-note behaviour for Payment/WorkOrder (and scanned
/// documents) until they grow their own slot rules.
/// </summary>
public static class VoiceSlots
{
    /// <summary>
    /// Value a user can give to say a slot intentionally has no value
    /// (e.g. an expense not tied to a specific property). It is non-empty, so it
    /// satisfies the slot like any other answer; the constant documents intent.
    /// </summary>
    public const string NoneSentinel = "none";

    private static readonly IReadOnlyDictionary<string, string[]> RequiredByType =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Expense"] = ["amount", "property_id"],
        };

    /// <summary>Alternate field names that satisfy a slot (the LLM may emit "total").</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Aliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["amount"] = ["amount", "total"],
        };

    private static readonly IReadOnlyDictionary<string, string> Prompts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["amount"] = "How much was it?",
            ["property_id"] =
                "Which property is this for? You can also say it isn't for a specific property.",
        };

    public static SlotEvaluation Evaluate(
        string? recordType,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        var required = recordType is not null && RequiredByType.TryGetValue(recordType, out var slots)
            ? slots
            : [];

        var missing = new List<string>();
        foreach (var slot in required)
        {
            if (!IsSatisfied(slot, fieldValues))
            {
                missing.Add(slot);
            }
        }

        var nextPrompt = missing.Count > 0 ? PromptFor(missing[0]) : null;
        return new SlotEvaluation(missing, nextPrompt, missing.Count == 0);
    }

    private static bool IsSatisfied(string slot, IReadOnlyDictionary<string, string> values)
    {
        var names = Aliases.TryGetValue(slot, out var alts) ? alts : [slot];
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }
        return false;
    }

    private static string PromptFor(string slot) =>
        Prompts.TryGetValue(slot, out var prompt)
            ? prompt
            : $"What's the {slot.Replace('_', ' ')}?";
}

/// <summary>Result of evaluating a draft's required slots.</summary>
/// <param name="Missing">Required slots still empty, in ask order.</param>
/// <param name="NextPrompt">Short question for the first missing slot, or null when complete.</param>
/// <param name="Complete">True when no required slots are missing.</param>
public sealed record SlotEvaluation(
    IReadOnlyList<string> Missing,
    string? NextPrompt,
    bool Complete);
