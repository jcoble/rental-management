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
/// MVP wire shape for one <see cref="AuditLog"/> row in the unified audit viewer. The trail is
/// append-only, so this is a read-only projection. The raw old/new JSON and IP address are
/// intentionally NOT exposed here — they belong to the deferred admin deep-view.
/// </summary>
public class AuditEntryResponse
{
    public int Id { get; set; }
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

    /// <summary>Stable selector for frontend tests, e.g. <c>audit-1</c>.</summary>
    public string TestId => $"audit-{Id}";

    /// <summary>
    /// Sanitized field-level diff for an <c>Updated</c> row (friendly name + old→new), so the History
    /// card can reveal *what* changed in-place. Empty for Created/Deleted (the description suffices)
    /// and when no diff builder is supplied.
    /// </summary>
    public IReadOnlyList<AuditFieldChange> Changes { get; set; } = Array.Empty<AuditFieldChange>();

    public static AuditEntryResponse FromEntity(
        AuditLog e,
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
            Changes = diff?.Build(e) ?? Array.Empty<AuditFieldChange>(),
        };

    /// <summary>
    /// Resolves a human-readable actor label. Precedence: the row's own <see cref="AuditLog.ActorLabel"/>
    /// (set for system/AI actors and HTTP requests that carried a name claim) → the user's display
    /// name/email resolved by the database projection in <paramref name="resolvedActorName"/> when
    /// only a <see cref="AuditLog.UserId"/>
    /// is present → "system" for actor-less rows. The bare "User #{id}" is a last resort only when a
    /// user id has no resolvable account (e.g. a since-deleted user), never the normal case.
    /// </summary>
    internal static string ResolveActor(AuditLog e, string? resolvedActorName = null)
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
        "TenantAccount" when unitId is > 0 => $"/units/{unitId}?tab=ledger&tenantAccount={entityId}",
        "TenantAccount" => $"/tenant-accounts/{entityId}",
        "Expense" when unitId is > 0 => $"/units/{unitId}?tab=ledger&ledger=expenses&expense={entityId}",
        "Expense" => $"/accounting/expenses/{entityId}",
        "LeaseManagement" when unitId is > 0 => $"/units/{unitId}?tab=lease&leaseManagement={entityId}",
        "LeaseManagement" => $"/lease-managements/{entityId}",
        "LeaseAgreement" when unitId is > 0 => $"/units/{unitId}?tab=lease&agreement={entityId}",
        "LeaseAgreement" => $"/lease-agreements/{entityId}",
        "Tenant" => $"/tenants/{entityId}",
        "Property" => $"/properties/{entityId}",
        "WorkOrder" when unitId is > 0 => $"/units/{unitId}?tab=maintenance&wo={entityId}",
        "WorkOrder" => $"/maintenance/{entityId}",
        "Vendor" => $"/vendors/{entityId}",
        "OwnerEntity" => "/owners",
        "Appointment" => $"/appointments/{entityId}",
        "Inspection" => $"/maintenance/inspections/{entityId}",
        "RentalApplication" when unitId is > 0 => $"/units/{unitId}?tab=applications&app={entityId}",
        "RentalApplication" => $"/applications/{entityId}",
        _ => null,
    };
}

/// <summary>
/// Admin-only forensic projection of one <see cref="AuditLog"/> row. Extends the landlord-facing
/// <see cref="AuditEntryResponse"/> with the fields it intentionally withholds — the actor's raw IP
/// address and the unredacted old→new JSON — for compliance / forensic review. Surfaced only via the
/// Admin-gated <c>GET /api/v1/admin/audit</c>; the data already lives on the row (no migration).
/// </summary>
public sealed class AdminAuditEntryResponse
{
    public int Id { get; set; }
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

    // Forensic fields held back from the landlord-facing DTO.
    public string? IpAddress { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? ChangeReason { get; set; }

    public string TestId => $"admin-audit-{Id}";

    public static AdminAuditEntryResponse FromEntity(
        AuditLog e,
        AuditDescriber describer,
        string? resolvedActorName = null,
        int? unitId = null) => new()
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
            IpAddress = e.IpAddress,
            OldValues = e.OldValues,
            NewValues = e.NewValues,
            ChangeReason = e.ChangeReason,
        };
}
