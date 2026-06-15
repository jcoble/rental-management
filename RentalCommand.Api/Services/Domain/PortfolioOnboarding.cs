using System.Text.Json;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// The first-login Sandbox-vs-Live decision, persisted inside the portfolio's <c>Settings</c> JSON
/// (no dedicated column — see <see cref="PortfolioOnboarding"/>).
/// </summary>
public enum OnboardingChoice
{
    /// <summary>No choice made yet — the account must be routed through the first-login choice gate.</summary>
    Pending,

    /// <summary>The user chose "Explore with sample data": the portfolio is a seeded demo Sandbox.</summary>
    Sandbox,

    /// <summary>The user chose "Set up my real portfolio": an empty Live portfolio, no demo data.</summary>
    Live,
}

/// <summary>
/// Reads/writes the first-login onboarding choice in a portfolio's <c>Settings</c> JSON blob under the
/// <c>onboarding.choice</c> key. Stored in the existing jsonb <c>Settings</c> column (mirrors the
/// notification-email convention in <c>NotificationService</c>) so the choice persists without a schema
/// migration. The merge is non-destructive: it preserves any sibling settings (e.g. notifications).
/// </summary>
public static class PortfolioOnboarding
{
    private const string OnboardingKey = "onboarding";
    private const string ChoiceKey = "choice";

    private const string PendingValue = "pending";
    private const string SandboxValue = "sandbox";
    private const string LiveValue = "live";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Reads the persisted choice from a portfolio's <c>Settings</c> JSON. Absent / malformed settings,
    /// or an unrecognized value, read as <see cref="OnboardingChoice.Pending"/> so a brand-new (or
    /// legacy) account is routed through the gate by default.
    /// </summary>
    public static OnboardingChoice ReadChoice(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return OnboardingChoice.Pending;
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty(OnboardingKey, out var onboarding) &&
                onboarding.ValueKind == JsonValueKind.Object &&
                onboarding.TryGetProperty(ChoiceKey, out var choice) &&
                choice.ValueKind == JsonValueKind.String)
            {
                return ParseChoice(choice.GetString());
            }
        }
        catch
        {
            return OnboardingChoice.Pending;
        }

        return OnboardingChoice.Pending;
    }

    /// <summary>True when the account has not yet made the Sandbox-vs-Live decision.</summary>
    public static bool IsPending(string? settingsJson) => ReadChoice(settingsJson) == OnboardingChoice.Pending;

    /// <summary>
    /// Returns a new <c>Settings</c> JSON string with <c>onboarding.choice</c> set to
    /// <paramref name="choice"/>, preserving every other key already present in
    /// <paramref name="settingsJson"/>.
    /// </summary>
    public static string WriteChoice(string? settingsJson, OnboardingChoice choice)
    {
        var root = ParseRoot(settingsJson);

        Dictionary<string, object?> onboarding;
        if (root.TryGetValue(OnboardingKey, out var existing) &&
            existing is JsonElement element &&
            element.ValueKind == JsonValueKind.Object)
        {
            onboarding = JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText())
                         ?? new Dictionary<string, object?>();
        }
        else if (existing is Dictionary<string, object?> existingDict)
        {
            onboarding = existingDict;
        }
        else
        {
            onboarding = new Dictionary<string, object?>();
        }

        onboarding[ChoiceKey] = ToValue(choice);
        root[OnboardingKey] = onboarding;
        return JsonSerializer.Serialize(root, WriteOptions);
    }

    private static Dictionary<string, object?> ParseRoot(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(settingsJson)
                   ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static OnboardingChoice ParseChoice(string? value) => value switch
    {
        SandboxValue => OnboardingChoice.Sandbox,
        LiveValue => OnboardingChoice.Live,
        _ => OnboardingChoice.Pending,
    };

    private static string ToValue(OnboardingChoice choice) => choice switch
    {
        OnboardingChoice.Sandbox => SandboxValue,
        OnboardingChoice.Live => LiveValue,
        _ => PendingValue,
    };
}
