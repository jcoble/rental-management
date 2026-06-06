using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Auditing;

/// <summary>
/// Turns a raw <see cref="AuditLog"/> row into one plain-English sentence for a non-technical
/// landlord, e.g. "Recorded a payment", "Updated lease", "Deleted expense". Deterministic — no LLM.
/// Soft-deletes already arrive as <see cref="AuditLogOperation.Deleted"/> from the interceptor, so a
/// delete reads as "Deleted {entity}". Falls back to "{Operation} {EntityType} #{Id}" for anything
/// unmapped, so a new audited entity is never blank.
/// </summary>
public sealed class AuditDescriber
{
    public string Describe(AuditLog row)
    {
        var noun = EntityNoun(row.EntityType);

        return row.Operation switch
        {
            AuditLogOperation.Created => Created(row.EntityType, noun),
            AuditLogOperation.Updated => $"Updated {noun}",
            AuditLogOperation.Deleted => $"Deleted {noun}",
            AuditLogOperation.Approved => $"Approved {noun}",
            AuditLogOperation.Rejected => $"Rejected {noun}",
            _ => $"{row.Operation} {row.EntityType} #{row.EntityId}",
        };
    }

    // "Created" reads better as a domain verb for several types ("Recorded a payment").
    private static string Created(string entityType, string noun) => entityType switch
    {
        "Payment" => "Recorded a payment",
        "Expense" => "Recorded an expense",
        "WorkOrder" => "Created a work order",
        "Appointment" => "Scheduled an appointment",
        "Inspection" => "Scheduled an inspection",
        "RentalApplication" => "Received a rental application",
        _ => $"Added {noun}",
    };

    private static string EntityNoun(string entityType) => entityType switch
    {
        "Payment" => "payment",
        "Expense" => "expense",
        "Lease" => "lease",
        "Tenant" => "tenant",
        "Property" => "property",
        "WorkOrder" => "work order",
        "Vendor" => "vendor",
        "OwnerEntity" => "owner",
        "Appointment" => "appointment",
        "Inspection" => "inspection",
        "RentalApplication" => "rental application",
        _ => entityType,
    };
}
