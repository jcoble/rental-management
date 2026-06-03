using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

public static class WorkOrderExtractionSchema
{
    public const string PromptId = "maintenance-photo-to-work-order-v1";

    public const string Instructions =
        "You are extracting a maintenance work order from a photo or note for a US residential-rental property manager. " +
        "Identify the likely property, unit, tenant, category, urgency, and a concise title/description. " +
        "Use property_id, unit_id, tenant_id, and vendor_id only when the grounding context strongly matches; otherwise leave them empty for human review. " +
        "Priority must be Low, Normal, High, or Emergency. Use Emergency for active flooding, fire, electrical hazard, no heat in winter, lockout, or safety risk. " +
        "Use High for urgent habitability problems or worsening leaks. Use Normal for routine repairs and Low for cosmetic or preventive items. " +
        "Never invent ids, costs, or facts not visible in the document/photo or grounding context.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } = new[]
    {
        new ExtractionFieldSpec("target_entity_type", "enum",
            "Always set to WorkOrder for this schema.",
            EnumValues: new[] { "WorkOrder" }),
        new ExtractionFieldSpec("property_id", "integer",
            "Matched property id from grounding context, only when strongly matched."),
        new ExtractionFieldSpec("unit_id", "integer",
            "Matched unit id from grounding context, only when strongly matched."),
        new ExtractionFieldSpec("tenant_id", "integer",
            "Matched tenant id from grounding context, only when strongly matched."),
        new ExtractionFieldSpec("lease_id", "integer",
            "Matched lease id from grounding context, only when strongly matched."),
        new ExtractionFieldSpec("vendor_id", "integer",
            "Suggested vendor id from grounding context, only when strongly matched."),
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
