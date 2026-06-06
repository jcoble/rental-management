using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

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

    public static AuditEntryResponse FromEntity(AuditLog e, AuditDescriber describer) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        Operation = e.Operation,
        OperationName = e.Operation.ToString(),
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        Actor = ResolveActor(e),
        Description = describer.Describe(e),
        DetailHref = BuildDetailHref(e.EntityType, e.EntityId),
        Timestamp = e.Timestamp,
    };

    private static string ResolveActor(AuditLog e)
    {
        if (!string.IsNullOrWhiteSpace(e.ActorLabel))
        {
            return e.ActorLabel!;
        }

        return e.UserId.HasValue ? $"User #{e.UserId.Value}" : "system";
    }

    /// <summary>Maps an entity type + id to its web detail route (null when there is no page).</summary>
    private static string? BuildDetailHref(string entityType, int entityId) => entityType switch
    {
        "Payment" => $"/accounting/payments/{entityId}",
        "Expense" => $"/accounting/expenses/{entityId}",
        "Lease" => $"/leases/{entityId}",
        "Tenant" => $"/tenants/{entityId}",
        "Property" => $"/properties/{entityId}",
        "WorkOrder" => $"/work-orders/{entityId}",
        "Vendor" => "/owners",
        "OwnerEntity" => "/owners",
        "Appointment" => $"/appointments/{entityId}",
        "Inspection" => $"/inspections/{entityId}",
        "RentalApplication" => $"/applications/{entityId}",
        _ => null,
    };
}
