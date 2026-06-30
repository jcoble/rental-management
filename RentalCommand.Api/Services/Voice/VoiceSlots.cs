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
/// V1 only supports voice-created expense drafts. Other detected intents are
/// returned as ambiguous so the client keeps the user in the conversation and
/// never offers a one-tap save for a deferred record type.
/// </summary>
public static class VoiceSlots
{
    // Required slots per record type. Expense v1 requires only a positive
    // amount — that's the one field the extraction always needs and that the
    // confirm step rejects when missing. A spoken property is attached when the
    // model resolves it (see the Expense prompt) but is optional, so the
    // conversation can complete without one (the receipt isn't always for a
    // specific unit).
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

    private const string UnsupportedIntentPrompt =
        "I can save expenses by voice right now. Tell me the expense amount and vendor, or review this draft manually.";

    public static SlotEvaluation Evaluate(
        string? recordType,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        if (IsTruthy(fieldValues, "voice_ambiguous") ||
            recordType is null ||
            !RequiredByType.TryGetValue(recordType, out var required))
        {
            return new SlotEvaluation([], UnsupportedIntentPrompt, false, true);
        }

        var missing = new List<string>();
        foreach (var slot in required)
        {
            if (!IsSatisfied(slot, fieldValues))
            {
                missing.Add(slot);
            }
        }

        var nextPrompt = missing.Count > 0 ? PromptFor(missing[0]) : null;
        return new SlotEvaluation(missing, nextPrompt, missing.Count == 0, false);
    }

    private static bool IsTruthy(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var raw))
            return false;

        return raw.Trim().ToLowerInvariant() is "true" or "1" or "yes";
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
/// <param name="Ambiguous">True when the voice turn needs clarification before it can be saved.</param>
public sealed record SlotEvaluation(
    IReadOnlyList<string> Missing,
    string? NextPrompt,
    bool Complete,
    bool Ambiguous);
