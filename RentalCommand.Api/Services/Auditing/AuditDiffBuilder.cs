using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Auditing;

/// <summary>
/// Turns an <see cref="AtomicAuditLog"/> row's raw <c>OldValues</c>/<c>NewValues</c> JSON into a small,
/// landlord-safe list of field-level changes ("amount: $32,423 → $23,423", "status: Pending → Paid")
/// for the per-record History card. This is the customer-facing, *sanitized* projection: friendly
/// field names + formatted values, with no raw JSON, no IP, and no plumbing/PII fields. The raw
/// old→new JSON remains separate from this sanitized change list on the audit DTOs.
///
/// <para>The interceptor serializes the changed scalars as a flat <c>{ PropertyName: value }</c>
/// JSON object captured by the atomic audit interceptor. For an <c>Updated</c>
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

    // Snapshot rows are full entity payloads. They are deliberately fail-closed: a field must match
    // one of these small, human-meaningful patterns or an explicit entity addition below before it
    // can render. This is separate from Updated behavior, which continues to show changed fields as
    // it did before this review fix.
    private static readonly Regex[] SharedSafeSnapshotFieldPatterns =
    [
        new("^(?:Amount|Balance|Category|Count|Currency|Description|Frequency|Interest|Interval|LateFee|Method|Name|Notes?|Operation|PaymentMethod|Percent|Principal|Quantity|Rate|Reason|Rent|Status|Subtotal|Title|Total|Type|UnitNumber|Value)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new("^Is[A-Z][A-Za-z0-9]*$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
    ];

    private static readonly IReadOnlyDictionary<string, HashSet<string>> EntitySafeSnapshotFields =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(OwnerEntity)] = new(StringComparer.OrdinalIgnoreCase)
            {
                "OwnerEntityType", "IsPrimary",
            },
            [nameof(Property)] = new(StringComparer.OrdinalIgnoreCase)
            {
                "PropertyType", "IsActive",
            },
            [nameof(Unit)] = new(StringComparer.OrdinalIgnoreCase)
            {
                "UnitType", "Bedrooms", "Bathrooms", "MonthlyRent", "IsActive",
            },
            [nameof(RentalApplication)] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Status", "DesiredMoveInDate", "Employer", "MonthlyIncome", "ConsentGiven", "DecisionReason",
            },
            [nameof(TenantAccount)] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Currency", "RentTrackingStartOn", "OpenedAtUtc", "ClosedAtUtc", "CloseReasonCode", "CloseNote",
            },
            [nameof(LeaseManagement)] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Status", "StartDate", "EndDate", "MonthlyRent", "SecurityDeposit", "MoveInDate", "MoveOutDate",
            },
            [nameof(LeaseAgreement)] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Status", "EffectiveOn", "ExpiresOn", "AgreementNumber",
            },
            [nameof(WorkOrder)] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Status", "Priority", "Title", "ScheduledFor", "CompletedAt",
            },
            [nameof(Expense)] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ExpenseDate", "BillableToOwner", "PaymentMethod", "VendorId", "PropertyId", "UnitId", "WorkOrderId",
            },
            ["Payment"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "PaymentDate", "PaymentMethod", "Status", "Reference", "TenantId", "LeaseManagementId",
            },
        };

    // Keep this denylist ahead of the allowlist so a future shared pattern or entity addition cannot
    // accidentally re-enable a known credential, identity, account, birth-date, or contact field.
    private static readonly Regex SensitiveSnapshotFieldPattern = new(
        "(?:ssn|taxid|password|secret|token|apikey|routing|account.?number|dateofbirth|birth|dob|ipaddress|email|phone|address|extract|pay.?stub|ocr)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Lazy<IReadOnlyDictionary<string, Type>> EnumPropertyTypes =
        new(BuildEnumPropertyTypes);

    private static readonly Dictionary<string, string> EnumLabels = new(StringComparer.Ordinal)
    {
        ["InProgress"] = "In progress",
        ["NeedsFollowUp"] = "Needs follow-up",
        ["NeedsReconnect"] = "Needs reconnect",
        ["NoShow"] = "No show",
        ["NoticeGiven"] = "Notice given",
        ["OnHold"] = "On hold",
        ["PaidOff"] = "Paid off",
        ["PartiallyReturned"] = "Partially returned",
        ["PartiallySigned"] = "Partially signed",
        ["PendingSignature"] = "Pending signature",
        ["UnderMaintenance"] = "Under maintenance",
        ["UnderReview"] = "Under review",
        ["WaitingParts"] = "Waiting on parts",
    };

    /// <summary>
    /// Build the sanitized field-level diff for one audit row. Updated rows contain old→new values;
    /// Created rows contain each allowlisted captured field as set to a non-null scalar value; Deleted
    /// rows contain each allowlisted captured field as having been a non-null scalar value. Plumbing
    /// fields are omitted in every operation. Updated rows retain their existing changed-field behavior.
    /// </summary>
    public IReadOnlyList<AuditFieldChange> Build(AtomicAuditLog row)
    {
        if (row.Operation is not (AuditLogOperation.Updated or AuditLogOperation.Created or AuditLogOperation.Deleted))
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

        var keys = row.Operation switch
        {
            AuditLogOperation.Created => newValues.Keys,
            AuditLogOperation.Deleted => oldValues.Keys,
            _ => newValues.Keys.Concat(oldValues.Keys),
        };

        foreach (var key in keys)
        {
            if (!seen.Add(key) || HiddenFields.Contains(key))
            {
                continue;
            }

            oldValues.TryGetValue(key, out var oldEl);
            newValues.TryGetValue(key, out var newEl);

            var isSnapshot = row.Operation is AuditLogOperation.Created or AuditLogOperation.Deleted;
            if (isSnapshot)
            {
                var snapshotValue = row.Operation == AuditLogOperation.Created ? newEl : oldEl;
                if (snapshotValue.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    || snapshotValue.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    || !IsSafeSnapshotField(row.EntityType, key))
                {
                    continue;
                }
            }

            var oldText = row.Operation == AuditLogOperation.Created
                ? "—"
                : Format(row.EntityType, key, oldEl);
            var newText = row.Operation == AuditLogOperation.Deleted
                ? "—"
                : Format(row.EntityType, key, newEl);

            // No visible change (e.g. only redaction noise) — skip updates. Snapshot nulls were
            // skipped above so they never become noisy "set to —" / "was —" lines.
            if (row.Operation == AuditLogOperation.Updated
                && string.Equals(oldText, newText, StringComparison.Ordinal))
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

    private static bool IsSafeSnapshotField(string entityType, string field)
    {
        if (SensitiveSnapshotFieldPattern.IsMatch(field))
        {
            return false;
        }

        if (SharedSafeSnapshotFieldPatterns.Any(pattern => pattern.IsMatch(field)))
        {
            return true;
        }

        return EntitySafeSnapshotFields.TryGetValue(entityType, out var additions)
            && additions.Contains(field);
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
    private static string Format(string entityType, string field, JsonElement el)
    {
        if (TryFormatKnownEnum(entityType, field, el, out var enumText))
        {
            return enumText;
        }

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

    private static bool TryFormatKnownEnum(string entityType, string field, JsonElement el, out string text)
    {
        text = string.Empty;
        if (el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return false;
        }

        if (!EnumPropertyTypes.Value.TryGetValue($"{entityType}.{field}", out var enumType))
        {
            return false;
        }

        object? enumValue = null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var numeric))
        {
            enumValue = Enum.ToObject(enumType, numeric);
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            var raw = el.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (!Enum.TryParse(enumType, raw, ignoreCase: true, out enumValue)
                && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                enumValue = Enum.ToObject(enumType, numeric);
            }
        }

        if (enumValue is null || !Enum.IsDefined(enumType, enumValue))
        {
            return false;
        }

        var enumName = Enum.GetName(enumType, enumValue);
        if (string.IsNullOrWhiteSpace(enumName))
        {
            return false;
        }

        text = FormatEnumLabel(enumName);
        return true;
    }

    private static IReadOnlyDictionary<string, Type> BuildEnumPropertyTypes()
    {
        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var entityAssembly = typeof(AtomicAuditLog).Assembly;
        foreach (var type in entityAssembly.GetTypes().Where(t => t.IsClass && t.Namespace == typeof(AtomicAuditLog).Namespace))
        {
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                if (!propType.IsEnum)
                {
                    continue;
                }

                map[$"{type.Name}.{prop.Name}"] = propType;
            }
        }

        return map;
    }

    private static string FormatEnumLabel(string enumName) => EnumLabels.TryGetValue(enumName, out var label)
        ? label
        : Humanize(enumName);

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
