using System.Text.Json;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>
/// One sanitized, landlord-safe field change inside an <see cref="AuditEntryResponse"/>: a friendly
/// field name with its formatted old → new values (e.g. <c>Amount</c>, <c>$32,423</c> → <c>$23,423</c>).
/// Built server-side from the raw old/new JSON by <see cref="AuditDiffBuilder"/>; never carries raw
/// JSON, IP, or PII. The unredacted JSON stays on the Admin-only forensic DTO.
/// </summary>
public sealed class AuditFieldChange
{
    /// <summary>Humanized field label, e.g. "Payment method".</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Formatted prior value, or "—" when there was none.</summary>
    public string OldValue { get; set; } = string.Empty;

    /// <summary>Formatted new value, or "—" when cleared.</summary>
    public string NewValue { get; set; } = string.Empty;
}

/// <summary>
/// Wire shape for one <see cref="AtomicAuditLog"/> row in the unified audit viewer. The trail is
/// append-only, so this is a read-only projection. Only the portfolio-user-authored reason and the
/// server-built sanitized change lines cross this landlord-facing boundary; IP addresses and raw
/// before/after snapshots stay on the admin-only forensic projection.
/// </summary>
public class AuditEntryResponse
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public AuditLogOperation Operation { get; set; }

    /// <summary>Operation as its string name (e.g. <c>Created</c>).</summary>
    public string OperationName { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }

    /// <summary>Who did it: the actor label, else "User #{id}", else "system".</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Plain-English summary for a non-technical landlord (via <see cref="AuditDescriber"/>).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Web detail route for the audited record, or null when the type has no detail page.</summary>
    public string? DetailHref { get; set; }

    public DateTime Timestamp { get; set; }

    /// <summary>Optional reason captured by the write path for this change.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>audit-1</c>.</summary>
    public string TestId => $"audit-{Id}";

    /// <summary>
    /// Sanitized field-level diff for an audit row (friendly name + formatted old→new), so the History
    /// card can reveal *what* changed in-place. Created/Deleted rows use an empty-side snapshot, and
    /// the list is empty when no diff builder is supplied.
    /// </summary>
    public IReadOnlyList<AuditFieldChange> Changes { get; set; } = Array.Empty<AuditFieldChange>();

    public static AuditEntryResponse FromEntity(
        AtomicAuditLog e,
        AuditDescriber describer,
        AuditDiffBuilder? diff = null,
        string? resolvedActorName = null,
        int? unitId = null) => new()
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            Operation = e.Operation,
            OperationName = e.Operation.ToString(),
            EntityType = e.EntityType,
            EntityId = e.EntityId,
            Actor = ResolveActor(e, resolvedActorName),
            Description = describer.Describe(e),
            DetailHref = BuildDetailHref(e.EntityType, e.EntityId, unitId),
            Timestamp = e.Timestamp,
            ChangeReason = e.ChangeReason,
            Changes = diff?.Build(e) ?? Array.Empty<AuditFieldChange>(),
        };

    /// <summary>
    /// Resolves a human-readable actor label. Precedence: the row's own <see cref="AtomicAuditLog.ActorLabel"/>
    /// (set for system/AI actors and HTTP requests that carried a name claim) → the user's display
    /// name/email resolved by the database projection in <paramref name="resolvedActorName"/> when
    /// only a <see cref="AtomicAuditLog.UserId"/>
    /// is present → "system" for actor-less rows. The bare "User #{id}" is a last resort only when a
    /// user id has no resolvable account (e.g. a since-deleted user), never the normal case.
    /// </summary>
    internal static string ResolveActor(AtomicAuditLog e, string? resolvedActorName = null)
    {
        if (!string.IsNullOrWhiteSpace(e.ActorLabel))
        {
            return e.ActorLabel!;
        }

        if (e.UserId.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(resolvedActorName))
            {
                return resolvedActorName;
            }

            return $"User #{e.UserId.Value}";
        }

        return "system";
    }

    /// <summary>Maps an entity type + id to its web detail route (null when there is no page).</summary>
    internal static string? BuildDetailHref(string entityType, int entityId, int? unitId = null) => entityType switch
    {
        "TenantAccount" when unitId is > 0 => $"/units/{unitId}?tab=money&tenantAccount={entityId}",
        "TenantAccount" => $"/tenant-accounts/{entityId}",
        "Expense" when unitId is > 0 => $"/units/{unitId}?tab=money&ledger=expenses&expense={entityId}",
        "Expense" => $"/accounting/expenses/{entityId}",
        "LeaseManagement" when unitId is > 0 => $"/units/{unitId}?tab=tenant-lease&view=agreements&leaseManagement={entityId}",
        "LeaseManagement" => $"/lease-managements/{entityId}",
        "LeaseAgreement" when unitId is > 0 => $"/units/{unitId}?tab=tenant-lease&view=agreements&agreement={entityId}",
        "LeaseAgreement" => $"/lease-agreements/{entityId}",
        "Tenant" => $"/tenants/{entityId}",
        "Property" => $"/properties/{entityId}",
        "WorkOrder" when unitId is > 0 => $"/units/{unitId}?tab=maintenance&wo={entityId}",
        "WorkOrder" => $"/maintenance/{entityId}",
        "Vendor" => $"/vendors/{entityId}",
        "OwnerEntity" => "/owners",
        "Appointment" => $"/appointments/{entityId}",
        "Inspection" => $"/maintenance/inspections/{entityId}",
        "RentalApplication" when unitId is > 0 => $"/units/{unitId}?tab=leasing&view=applications&app={entityId}",
        "RentalApplication" => $"/applications/{entityId}",
        _ => null,
    };
}

/// <summary>
/// Admin-only forensic projection of one <see cref="AtomicAuditLog"/> row. It adds actor identity and
/// keeps the captured raw detail grouped for compliance / forensic review. Surfaced only via the
/// Admin-gated <c>GET /api/v1/admin/audit</c>; the data already lives on the row (no migration).
/// </summary>
public sealed class AdminAuditEntryResponse
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public AuditLogOperation Operation { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }

    /// <summary>Resolved actor label (same as the landlord-facing view).</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Raw actor identity for forensics: the user id (null for system/AI actors) and label.</summary>
    public int? UserId { get; set; }
    public string? ActorLabel { get; set; }

    public string Description { get; set; } = string.Empty;
    public string? DetailHref { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>Sanitized field-level changes for the expanded forensic row.</summary>
    public IReadOnlyList<AuditFieldChange> Changes { get; set; } = Array.Empty<AuditFieldChange>();

    // Forensic fields retained on the admin projection for the operator view.
    public string? IpAddress { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? ChangeReason { get; set; }
    public bool IsProviderPaymentDeadLetter { get; set; }
    public string? ProviderEventId { get; set; }
    public string? PaymentIntentId { get; set; }
    public decimal? PaymentAmount { get; set; }
    public string? PaymentCurrency { get; set; }
    public long? TargetChargeLedgerEntryId { get; set; }
    public long? PaymentAttemptId { get; set; }
    public DateTime? ProviderReceivedAtUtc { get; set; }
    public DateTime? ProviderDeadLetteredAtUtc { get; set; }
    public string? RecoveryStatus { get; set; }
    public string? RecoveryAction { get; set; }

    public string TestId => $"admin-audit-{Id}";

    public static AdminAuditEntryResponse FromEntity(
        AtomicAuditLog e,
        AuditDescriber describer,
        string? resolvedActorName = null,
        int? unitId = null,
        AuditDiffBuilder? diff = null)
    {
        var provider = ProviderPaymentAuditFields.Parse(e);
        return new()
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            Operation = e.Operation,
            OperationName = e.Operation.ToString(),
            EntityType = e.EntityType,
            EntityId = e.EntityId,
            Actor = AuditEntryResponse.ResolveActor(e, resolvedActorName),
            UserId = e.UserId,
            ActorLabel = e.ActorLabel,
            Description = describer.Describe(e),
            DetailHref = AuditEntryResponse.BuildDetailHref(e.EntityType, e.EntityId, unitId),
            Timestamp = e.Timestamp,
            Changes = diff?.Build(e) ?? Array.Empty<AuditFieldChange>(),
            IpAddress = e.IpAddress,
            OldValues = e.OldValues,
            NewValues = e.NewValues,
            ChangeReason = e.ChangeReason,
            IsProviderPaymentDeadLetter = provider.IsDeadLetter,
            ProviderEventId = provider.EventId,
            PaymentIntentId = provider.PaymentIntentId,
            PaymentAmount = provider.Amount,
            PaymentCurrency = provider.Currency,
            TargetChargeLedgerEntryId = provider.TargetChargeLedgerEntryId,
            PaymentAttemptId = provider.PaymentAttemptId,
            ProviderReceivedAtUtc = provider.ReceivedAtUtc,
            ProviderDeadLetteredAtUtc = provider.DeadLetteredAtUtc,
            RecoveryStatus = provider.RecoveryStatus,
            RecoveryAction = provider.RecoveryAction,
        };
    }

    private sealed record ProviderPaymentAuditFields(
        bool IsDeadLetter,
        string? EventId,
        string? PaymentIntentId,
        decimal? Amount,
        string? Currency,
        long? TargetChargeLedgerEntryId,
        long? PaymentAttemptId,
        DateTime? ReceivedAtUtc,
        DateTime? DeadLetteredAtUtc,
        string? RecoveryStatus,
        string? RecoveryAction)
    {
        internal static ProviderPaymentAuditFields Parse(AtomicAuditLog audit)
        {
            if (audit.EntityType != nameof(TenantAccount)
                || (audit.ChangeReason?.Contains("dead-lettered", StringComparison.OrdinalIgnoreCase) != true
                    && audit.NewValues?.Contains("DeadLettered", StringComparison.OrdinalIgnoreCase) != true))
                return new(false, null, null, null, null, null, null, null, null, null, null);

            try
            {
                using var doc = JsonDocument.Parse(audit.NewValues ?? "{}");
                var root = doc.RootElement;
                return new(true,
                    StringValue(root, "ProviderEventId") ?? ExtractEventId(audit.ChangeReason),
                    StringValue(root, "ProviderObjectId"),
                    DecimalValue(root, "Amount"),
                    StringValue(root, "Currency"),
                    LongValue(root, "ChargeLedgerEntryId"),
                    LongValue(root, "Id"),
                    DateTimeValue(root, "ProviderReceivedAtUtc"),
                    DateTimeValue(root, "ProviderDeadLetteredAtUtc"),
                    StringValue(root, "RecoveryStatus") ?? "DeadLettered",
                    StringValue(root, "RecoveryAction"));
            }
            catch (JsonException)
            {
                return new(true, ExtractEventId(audit.ChangeReason), null, null, null,
                    null, null, null, null, "DeadLettered", null);
            }
        }

        private static string? StringValue(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        private static long? LongValue(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
                ? result : null;
        private static decimal? DecimalValue(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.TryGetDecimal(out var result)
                ? result : null;
        private static DateTime? DateTimeValue(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.TryGetDateTime(out var result)
                ? result : null;
        private static string? ExtractEventId(string? reason)
        {
            const string marker = "provider event ";
            var index = reason?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (index < 0) return null;
            var start = index + marker.Length;
            var end = reason!.IndexOf(' ', start);
            return (end < 0 ? reason[start..] : reason[start..end]).TrimEnd(':');
        }
    }
}
