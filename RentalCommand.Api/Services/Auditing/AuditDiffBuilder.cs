using System.Globalization;
using System.Text.Json;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Auditing;

/// <summary>
/// Turns an <see cref="AuditLog"/> row's raw <c>OldValues</c>/<c>NewValues</c> JSON into a small,
/// landlord-safe list of field-level changes ("amount: $32,423 → $23,423", "status: Pending → Paid")
/// for the per-record History card. This is the customer-facing, *sanitized* projection: friendly
/// field names + formatted values, with no raw JSON, no IP, and no plumbing/PII fields. The raw
/// old→new JSON stays on the Admin-only forensic DTO.
///
/// <para>The interceptor serializes the changed scalars as a flat <c>{ PropertyName: value }</c>
/// JSON object (see <see cref="Data.Auditing.AuditSaveChangesInterceptor"/>). For an <c>Updated</c>
/// row both dictionaries carry exactly the modified properties, so a diff is the union of their keys.</para>
/// </summary>
public sealed class AuditDiffBuilder
{
    // Plumbing / noisy columns that mean nothing to a landlord — never surfaced in the customer diff.
    private static readonly HashSet<string> HiddenFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "UpdatedAt", "CreatedAt", "DeletedAt", "PortfolioId", "Id",
        "ReceiptData", "RowVersion", "ConcurrencyStamp",
    };

    /// <summary>
    /// Build the sanitized field-level diff for one audit row. Only meaningful for <c>Updated</c> rows;
    /// Created/Deleted carry a full snapshot (not a diff) so they return an empty list — the row's
    /// humanized description already says what happened.
    /// </summary>
    public IReadOnlyList<AuditFieldChange> Build(AuditLog row)
    {
        if (row.Operation != AuditLogOperation.Updated)
        {
            return Array.Empty<AuditFieldChange>();
        }

        var oldValues = Parse(row.OldValues);
        var newValues = Parse(row.NewValues);
        if (oldValues.Count == 0 && newValues.Count == 0)
        {
            return Array.Empty<AuditFieldChange>();
        }

        var changes = new List<AuditFieldChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in newValues.Keys.Concat(oldValues.Keys))
        {
            if (!seen.Add(key) || HiddenFields.Contains(key))
            {
                continue;
            }

            oldValues.TryGetValue(key, out var oldEl);
            newValues.TryGetValue(key, out var newEl);

            var oldText = Format(oldEl);
            var newText = Format(newEl);

            // No visible change (e.g. only redaction noise) — skip.
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add(new AuditFieldChange
            {
                Field = Humanize(key),
                OldValue = oldText,
                NewValue = newText,
            });
        }

        return changes;
    }

    private static Dictionary<string, JsonElement> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            }

            var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Clone so the value survives the JsonDocument being disposed.
                map[prop.Name] = prop.Value.Clone();
            }

            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Render one JSON value as friendly display text (currency-agnostic numbers, dates, booleans).</summary>
    private static string Format(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return "—";

            case JsonValueKind.True:
                return "Yes";

            case JsonValueKind.False:
                return "No";

            case JsonValueKind.String:
                var s = el.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(s))
                {
                    return "—";
                }

                // ISO timestamps → short local-agnostic date.
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                {
                    return dt.TimeOfDay == TimeSpan.Zero
                        ? dt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
                        : dt.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture);
                }

                return s;

            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l))
                {
                    return l.ToString("#,##0", CultureInfo.InvariantCulture);
                }

                return el.GetDecimal().ToString("#,##0.##", CultureInfo.InvariantCulture);

            default:
                return el.ToString();
        }
    }

    /// <summary>Turn a PascalCase property name into Title-Case words ("PaymentMethod" → "Payment method").</summary>
    internal static string Humanize(string field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return field;
        }

        var sb = new System.Text.StringBuilder(field.Length + 4);
        for (var i = 0; i < field.Length; i++)
        {
            var c = field[i];
            if (i > 0 && char.IsUpper(c) && (!char.IsUpper(field[i - 1]) || (i + 1 < field.Length && char.IsLower(field[i + 1]))))
            {
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (i == 0)
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else
            {
                sb.Append(char.IsUpper(c) ? char.ToLowerInvariant(c) : c);
            }
        }

        return sb.ToString();
    }
}
