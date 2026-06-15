using System.Text.Json;
using System.Text.RegularExpressions;
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Parses, normalizes, and validates the landlord's application-form configuration. The raw config is
/// stored as a JSON object string on <c>Portfolio.ApplicationFormConfig</c>; this helper is the single
/// place that understands its shape so the public form and the landlord configurator never drift.
/// <para>
/// Fail-OPEN by design: any null/empty/malformed stored config normalizes to the original fixed-form
/// behavior (income enabled, pets off, no custom fields, no defaults, nothing locked).
/// </para>
/// </summary>
public static class ApplicationFormConfigParser
{
    /// <summary>Default keys that may appear in <c>locked</c>.</summary>
    public static readonly IReadOnlyList<string> LockableKeys = new[] { "propertyId", "unitId", "desiredMoveInDate" };

    // yyyy-MM-dd, lenient on the calendar (validated as a date below).
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // Stored config is read back by C# and by the web (camelCase) — keep camelCase on disk.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Parses the stored raw config into the normalized DTO. Returns sensible defaults for a
    /// null/empty/malformed value (fail-open to the original fixed form).
    /// </summary>
    public static ApplicationFormConfigDto Normalize(string? rawConfig)
    {
        var dto = new ApplicationFormConfigDto();
        if (string.IsNullOrWhiteSpace(rawConfig))
            return dto;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawConfig);
            // Clone so we can read after the document is disposed.
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return dto; // malformed → defaults
        }

        if (root.ValueKind != JsonValueKind.Object)
            return dto;

        // incomeSources.enabled (defaults to true when the section/object is absent)
        dto.IncomeSources = new IncomeSourcesConfig
        {
            Enabled = ReadBool(root, "incomeSources", "enabled", defaultValue: true),
        };

        // pets.{enabled,askDeposit} (default off)
        dto.Pets = new PetsConfig
        {
            Enabled = ReadBool(root, "pets", "enabled", defaultValue: false),
            AskDeposit = ReadBool(root, "pets", "askDeposit", defaultValue: false),
        };

        dto.CustomFields = ReadCustomFields(root);
        dto.Defaults = ReadDefaults(root);
        dto.Locked = ReadLocked(root);

        return dto;
    }

    /// <summary>
    /// Validates a landlord save request and produces the canonical JSON to persist. Throws
    /// <see cref="ApplicationFormConfigValidationException"/> on any rule violation. <paramref name="validPropertyIds"/>
    /// and <paramref name="validUnitIds"/> are the in-portfolio ids used to IDOR-guard the chosen defaults.
    /// </summary>
    public static string BuildCanonicalJson(
        SaveFormConfigRequest request,
        IReadOnlySet<int> validPropertyIds,
        IReadOnlySet<int> validUnitIds)
    {
        var errors = new List<string>();

        // ── Custom fields ──────────────────────────────────────────────────────
        var fields = new List<CustomFieldConfig>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < request.CustomFields.Count; i++)
        {
            var input = request.CustomFields[i];
            var label = (input.Label ?? string.Empty).Trim();
            if (label.Length == 0)
            {
                errors.Add($"Custom question #{i + 1} needs a label.");
                continue;
            }

            var type = (input.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (!CustomFieldTypes.IsValid(type))
            {
                errors.Add($"Custom question \"{label}\" has an unknown type.");
                continue;
            }

            var options = new List<string>();
            if (type == CustomFieldTypes.Select)
            {
                options = (input.Options ?? [])
                    .Select(o => (o ?? string.Empty).Trim())
                    .Where(o => o.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (options.Count == 0)
                    errors.Add($"Custom question \"{label}\" is a dropdown and needs at least one choice.");
            }

            var id = GenerateOrReuseId(input.Id, label, usedIds);
            fields.Add(new CustomFieldConfig
            {
                Id = id,
                Label = label,
                Type = type,
                Required = input.Required,
                Options = options,
            });
        }

        // ── Defaults (IDOR-guarded) ────────────────────────────────────────────
        int? propertyId = null;
        if (request.Defaults?.PropertyId is int pid && pid > 0)
        {
            if (validPropertyIds.Contains(pid)) propertyId = pid;
            else errors.Add("The default property is not in this portfolio.");
        }

        int? unitId = null;
        if (request.Defaults?.UnitId is int uid && uid > 0)
        {
            if (validUnitIds.Contains(uid)) unitId = uid;
            else errors.Add("The default unit is not in this portfolio.");
        }

        string? desiredMoveIn = null;
        var rawMoveIn = request.Defaults?.DesiredMoveInDate?.Trim();
        if (!string.IsNullOrEmpty(rawMoveIn))
        {
            if (IsoDate.IsMatch(rawMoveIn) && DateTime.TryParse(rawMoveIn, out _)) desiredMoveIn = rawMoveIn;
            else errors.Add("The default move-in date is not a valid date.");
        }

        // ── Locked ⊆ keys that actually have a default ─────────────────────────
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (propertyId is not null) available.Add("propertyId");
        if (unitId is not null) available.Add("unitId");
        if (desiredMoveIn is not null) available.Add("desiredMoveInDate");

        var locked = (request.Locked ?? [])
            .Select(l => (l ?? string.Empty).Trim())
            .Where(l => LockableKeys.Contains(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(available.Contains) // can only lock a default that exists
            .ToList();

        if (errors.Count > 0)
            throw new ApplicationFormConfigValidationException(errors);

        // ── Emit canonical JSON ────────────────────────────────────────────────
        var canonical = new ApplicationFormConfigDto
        {
            IncomeSources = new IncomeSourcesConfig { Enabled = request.IncomeSources?.Enabled ?? true },
            Pets = new PetsConfig
            {
                Enabled = request.Pets?.Enabled ?? false,
                AskDeposit = request.Pets?.AskDeposit ?? false,
            },
            CustomFields = fields,
            Defaults = new FormDefaultsConfig
            {
                PropertyId = propertyId,
                UnitId = unitId,
                DesiredMoveInDate = desiredMoveIn,
            },
            Locked = locked,
        };

        return JsonSerializer.Serialize(canonical, WriteOptions);
    }

    /// <summary>
    /// Filters a submitted custom-field answers payload down to ids that exist in the current config,
    /// coercing each value to the field's declared type. Returns the canonical answers JSON, or null
    /// when there are no valid answers. Drops unknown keys (anti-junk) and malformed input (fail-open).
    /// </summary>
    public static string? SanitizeCustomFieldAnswers(string? rawAnswers, ApplicationFormConfigDto config)
    {
        if (string.IsNullOrWhiteSpace(rawAnswers) || config.CustomFields.Count == 0)
            return null;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawAnswers);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var cleaned = new Dictionary<string, object?>();
        foreach (var field in config.CustomFields)
        {
            if (!root.TryGetProperty(field.Id, out var value))
                continue;

            switch (field.Type)
            {
                case CustomFieldTypes.YesNo:
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        cleaned[field.Id] = value.GetBoolean();
                    else if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b))
                        cleaned[field.Id] = b;
                    break;

                case CustomFieldTypes.Number:
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n))
                        cleaned[field.Id] = n;
                    else if (value.ValueKind == JsonValueKind.String
                             && decimal.TryParse(value.GetString(), out var ns))
                        cleaned[field.Id] = ns;
                    break;

                case CustomFieldTypes.Select:
                {
                    var s = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    // Only accept a value that is one of the configured options.
                    if (!string.IsNullOrEmpty(s)
                        && field.Options.Any(o => string.Equals(o, s, StringComparison.Ordinal)))
                        cleaned[field.Id] = s;
                    break;
                }

                default: // text
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var s = value.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            cleaned[field.Id] = s.Length > 2000 ? s[..2000] : s;
                    }
                    break;
            }
        }

        return cleaned.Count == 0 ? null : JsonSerializer.Serialize(cleaned, WriteOptions);
    }

    /// <summary>
    /// Validates and re-serializes a submitted income-sources array (drops empties; mirrors the first
    /// row out via <paramref name="firstEmployer"/>/<paramref name="firstIncome"/>). Returns null when
    /// there are no usable rows. Used only when the config has income enabled.
    /// </summary>
    public static string? SanitizeIncomeSources(
        string? rawIncome, out string? firstEmployer, out decimal? firstIncome)
    {
        firstEmployer = null;
        firstIncome = null;

        if (string.IsNullOrWhiteSpace(rawIncome))
            return null;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawIncome);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Array)
            return null;

        var rows = new List<Dictionary<string, object?>>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            string? employer = null;
            if (item.TryGetProperty("employer", out var emp) && emp.ValueKind == JsonValueKind.String)
            {
                var e = emp.GetString()?.Trim();
                if (!string.IsNullOrEmpty(e)) employer = e.Length > 200 ? e[..200] : e;
            }

            decimal? income = null;
            if (item.TryGetProperty("monthlyIncome", out var inc))
            {
                if (inc.ValueKind == JsonValueKind.Number && inc.TryGetDecimal(out var d)) income = d;
                else if (inc.ValueKind == JsonValueKind.String && decimal.TryParse(inc.GetString(), out var ds)) income = ds;
                if (income is < 0) income = null;
            }

            if (employer is null && income is null)
                continue;

            rows.Add(new Dictionary<string, object?>
            {
                ["employer"] = employer,
                ["monthlyIncome"] = income,
            });
        }

        if (rows.Count == 0)
            return null;

        firstEmployer = rows[0]["employer"] as string;
        firstIncome = rows[0]["monthlyIncome"] as decimal?;
        return JsonSerializer.Serialize(rows, WriteOptions);
    }

    /// <summary>
    /// Validates and re-serializes a submitted pets payload. Returns null when pets are off/unanswered.
    /// </summary>
    public static string? SanitizePets(string? rawPets)
    {
        if (string.IsNullOrWhiteSpace(rawPets))
            return null;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawPets);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var hasPets = root.TryGetProperty("hasPets", out var hp)
                      && hp.ValueKind is JsonValueKind.True;

        var pets = new List<Dictionary<string, string>>();
        if (hasPets && root.TryGetProperty("pets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var pet = new Dictionary<string, string>();
                foreach (var key in new[] { "type", "name", "breed", "weight" })
                {
                    if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(s)) pet[key] = s.Length > 120 ? s[..120] : s;
                    }
                }
                if (pet.Count > 0) pets.Add(pet);
            }
        }

        // "No, I don't have pets" is still a meaningful answer worth recording.
        if (!hasPets && pets.Count == 0)
            return JsonSerializer.Serialize(new { hasPets = false }, WriteOptions);

        return JsonSerializer.Serialize(new { hasPets, pets }, WriteOptions);
    }

    // ── readers ────────────────────────────────────────────────────────────────

    private static bool ReadBool(JsonElement root, string objectKey, string boolKey, bool defaultValue)
    {
        if (root.TryGetProperty(objectKey, out var obj) && obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(boolKey, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    private static IReadOnlyList<CustomFieldConfig> ReadCustomFields(JsonElement root)
    {
        if (!root.TryGetProperty("customFields", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var fields = new List<CustomFieldConfig>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var label = item.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            if (label.Length == 0)
                continue;

            var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()?.Trim().ToLowerInvariant() ?? CustomFieldTypes.Text
                : CustomFieldTypes.Text;
            if (!CustomFieldTypes.IsValid(type))
                type = CustomFieldTypes.Text;

            var required = item.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;

            var options = new List<string>();
            if (type == CustomFieldTypes.Select
                && item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray())
                {
                    if (o.ValueKind == JsonValueKind.String)
                    {
                        var s = o.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(s)) options.Add(s);
                    }
                }
            }

            var rawId = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            fields.Add(new CustomFieldConfig
            {
                Id = GenerateOrReuseId(rawId, label, usedIds),
                Label = label,
                Type = type,
                Required = required,
                Options = options,
            });
        }

        return fields;
    }

    private static FormDefaultsConfig ReadDefaults(JsonElement root)
    {
        var defaults = new FormDefaultsConfig();
        if (!root.TryGetProperty("defaults", out var d) || d.ValueKind != JsonValueKind.Object)
            return defaults;

        if (d.TryGetProperty("propertyId", out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out var pid) && pid > 0)
            defaults.PropertyId = pid;

        if (d.TryGetProperty("unitId", out var u) && u.ValueKind == JsonValueKind.Number
            && u.TryGetInt32(out var uid) && uid > 0)
            defaults.UnitId = uid;

        if (d.TryGetProperty("desiredMoveInDate", out var m) && m.ValueKind == JsonValueKind.String)
        {
            var s = m.GetString()?.Trim();
            if (!string.IsNullOrEmpty(s) && IsoDate.IsMatch(s)) defaults.DesiredMoveInDate = s;
        }

        return defaults;
    }

    private static IReadOnlyList<string> ReadLocked(JsonElement root)
    {
        if (!root.TryGetProperty("locked", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var locked = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;
            var s = item.GetString()?.Trim();
            if (!string.IsNullOrEmpty(s) && LockableKeys.Contains(s) && !locked.Contains(s))
                locked.Add(s);
        }
        return locked;
    }

    /// <summary>
    /// Reuses a provided id (when it's a sane slug) or derives a unique slug from the label, falling back
    /// to a short guid. Guarantees uniqueness within the field set.
    /// </summary>
    private static string GenerateOrReuseId(string? providedId, string label, HashSet<string> usedIds)
    {
        var candidate = (providedId ?? string.Empty).Trim();
        if (candidate.Length == 0 || !IsSaneId(candidate))
            candidate = Slugify(label);
        if (candidate.Length == 0)
            candidate = "field";

        var unique = candidate;
        var suffix = 2;
        while (!usedIds.Add(unique))
            unique = $"{candidate}-{suffix++}";
        return unique;
    }

    private static bool IsSaneId(string id) =>
        id.Length <= 64 && Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9_-]*$");

    private static string Slugify(string label)
    {
        var lower = label.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 48 ? slug[..48].Trim('-') : slug;
    }
}

/// <summary>Thrown when a landlord's save request fails validation. Carries the plain-language reasons.</summary>
public sealed class ApplicationFormConfigValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public ApplicationFormConfigValidationException(IReadOnlyList<string> errors)
        : base(errors.Count > 0 ? string.Join(" ", errors) : "The application form configuration is invalid.")
    {
        Errors = errors;
    }
}
