using System.Globalization;

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
    // Required slots per record type. Expense v1 requires only a positive
    // amount — that's the one field the extraction always needs and that the
    // confirm step rejects when missing. A spoken property is attached when the
    // model resolves it (see the Expense prompt) but is optional, so the
    // conversation can complete without one (the receipt isn't always for a
    // specific unit). Payment/WorkOrder have no slots yet (always complete).
    private static readonly IReadOnlyDictionary<string, string[]> RequiredByType =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Expense"] = ["amount"],
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
        var isAmount = string.Equals(slot, "amount", StringComparison.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // Amount slots need a *positive* number: the LLM emits "0"/"0.0" when
            // the speaker never said an amount, so a non-positive value must still
            // count as missing (and the confirm step rejects it anyway). Other
            // slots are satisfied by any non-empty value.
            if (!isAmount || IsPositiveAmount(value))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPositiveAmount(string raw)
    {
        var cleaned = new string(raw.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            && value > 0m;
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
