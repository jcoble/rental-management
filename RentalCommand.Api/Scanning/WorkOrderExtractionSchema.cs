using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

public static class WorkOrderExtractionSchema
{
    public const string PromptId = "maintenance-photo-to-work-order-v1";

    public const string Instructions =
        "You are extracting a maintenance work order from a photo or note for a US residential-rental property manager. " +
        "Identify the likely property, unit, tenant, category, urgency, and a concise title/description. " +
        "The grounding context lists this portfolio's known vendors, properties, units, and tenants, each with its exact numeric id. " +
        "For property_id, unit_id, tenant_id, and vendor_id, return the matching id COPIED VERBATIM from that grounding list, and only when it strongly matches; otherwise leave the id empty for human review. " +
        "Never return an id that is not present in the grounding list, and never guess or fabricate an id. " +
        "Priority must be Low, Normal, High, or Emergency. Use Emergency for active flooding, fire, electrical hazard, no heat in winter, lockout, or safety risk. " +
        "Use High for urgent habitability problems or worsening leaks. Use Normal for routine repairs and Low for cosmetic or preventive items. " +
        "Never invent ids, costs, or facts not visible in the document/photo or grounding context.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } = new[]
    {
        new ExtractionFieldSpec("target_entity_type", "enum",
            "Always set to WorkOrder for this schema.",
            EnumValues: new[] { "WorkOrder" }),
        new ExtractionFieldSpec("property_id", "integer",
            "Exact id of the matching property, copied from the grounding list's properties[].id, only when strongly matched. Empty otherwise."),
        new ExtractionFieldSpec("unit_id", "integer",
            "Exact id of the matching unit, copied from the grounding list's units[].id, only when strongly matched. Empty otherwise."),
        new ExtractionFieldSpec("tenant_id", "integer",
            "Exact id of the matching tenant, copied from the grounding list's tenants[].id, only when strongly matched. Empty otherwise."),
        new ExtractionFieldSpec("lease_id", "integer",
            "Leave empty; leases are not provided in the grounding list."),
        new ExtractionFieldSpec("vendor_id", "integer",
            "Exact id of the suggested vendor, copied from the grounding list's vendors[].id, only when strongly matched. Empty otherwise."),
        new ExtractionFieldSpec("title", "string",
            "Short maintenance title, e.g. 'Kitchen ceiling leak'.", Required: true),
        new ExtractionFieldSpec("description", "string",
            "Clear description of the issue and what can be seen or heard.", Required: true),
        new ExtractionFieldSpec("category", "string",
            "Maintenance category such as Plumbing, Electrical, HVAC, Appliance, Roofing, Pest, Safety, or General."),
        new ExtractionFieldSpec("priority", "enum",
            "Urgency: Low, Normal, High, or Emergency.",
            EnumValues: new[] { "Low", "Normal", "High", "Emergency" }),
        new ExtractionFieldSpec("estimated_cost", "number",
            "Estimated repair cost only if explicitly stated or obvious from the note; otherwise empty."),
        new ExtractionFieldSpec("notes", "string",
            "Short extra notes for reviewer, including uncertainty or vendor suggestion."),
    };
}
