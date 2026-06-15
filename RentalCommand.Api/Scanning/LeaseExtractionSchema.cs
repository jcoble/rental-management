using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Extraction schema for importing an existing residential lease agreement PDF into a Lease draft —
/// the "import your PDF leases" / "scan a stack of leases into an empty portfolio" migration unlock.
/// Mirrors <see cref="WorkOrderExtractionSchema"/>: property_id/unit_id are copied VERBATIM from the
/// grounded {id,name} lists and re-validated in-portfolio before they are ever trusted, so a
/// hallucinated or foreign id can never link another portfolio's row (IDOR).
///
/// Because a brand-new landlord scans leases into an EMPTY portfolio (no properties/units yet to
/// ground against), the schema ALSO captures the property's name/address and the unit number as free
/// text straight off the document. The confirm path matches that address to an existing in-portfolio
/// property (and unit) or, when none matches, CREATES them — so scanning a stack of leases bootstraps
/// properties → units → tenants → leases. tenant_name is likewise free text (no tenant_id) because an
/// imported lease's tenant is frequently not yet on file.
/// </summary>
public static class LeaseExtractionSchema
{
    public const string PromptId = "lease-pdf-to-lease-v2";

    public const string Instructions =
        "You are extracting the key terms of a US residential lease agreement (a signed or draft lease PDF) " +
        "for a property-management system, so the lease can be imported as a record. " +
        "Read the document and fill every field you can. " +
        "The grounding context lists this portfolio's known properties and units, each with its exact numeric id. " +
        "For property_id and unit_id, return the matching id COPIED VERBATIM from that grounding list, and only when " +
        "the lease's property/unit address strongly matches; otherwise leave the id empty for human review. " +
        "Never return an id that is not present in the grounding list, and never guess or fabricate an id. " +
        "ALWAYS also extract the leased premises straight from the document text, even when you matched an id: " +
        "property_address is the street address line of the leased premises (street number + name, e.g. '123 Maple Ave'); " +
        "property_city, property_state (2-letter), and property_postal_code are its city/state/ZIP; " +
        "property_name is a building or community name if the lease gives one (else leave empty). " +
        "unit_number is the apartment / unit / suite identifier of the leased premises (e.g. '4B', 'Apt 2', '101'); " +
        "leave it empty for a single-family home with no unit designation. " +
        "unit_bedrooms and unit_bathrooms are the unit's bed/bath counts (decimals, e.g. 1.5) and unit_square_feet its size, only if the lease states them. " +
        "For tenant_name, return the full name of the primary tenant/lessee exactly as written on the lease — do NOT return an id. " +
        "Dates must be ISO 8601 (YYYY-MM-DD). monthly_rent, security_deposit, and late_fee are decimal numbers with no currency symbol. " +
        "rent_due_day is the day of the month rent is due (1–31). " +
        "Leave unknown or absent fields empty — never invent values not present on the document.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } = new[]
    {
        new ExtractionFieldSpec("target_entity_type", "enum",
            "Always set to Lease for this schema.",
            EnumValues: new[] { "Lease" }),
        new ExtractionFieldSpec("tenant_name", "string",
            "Full name of the primary tenant / lessee as written on the lease. Free text, not an id.",
            Required: true),
        new ExtractionFieldSpec("property_id", "integer",
            "Exact id of the matching property, copied from the grounding list's properties[].id, only when strongly matched. Empty otherwise."),
        new ExtractionFieldSpec("unit_id", "integer",
            "Exact id of the matching unit, copied from the grounding list's units[].id, only when strongly matched. Empty otherwise."),
        new ExtractionFieldSpec("property_name", "string",
            "Building or community name of the leased premises, if the lease gives one (e.g. 'Maple Court Apartments'). Empty if the lease only has a street address."),
        new ExtractionFieldSpec("property_address", "string",
            "Street address line of the leased premises (street number + name, e.g. '123 Maple Ave'). Extract verbatim from the document; do not include the unit/apartment number here."),
        new ExtractionFieldSpec("property_city", "string",
            "City of the leased premises."),
        new ExtractionFieldSpec("property_state", "string",
            "State of the leased premises as a 2-letter US code (e.g. 'OH')."),
        new ExtractionFieldSpec("property_postal_code", "string",
            "ZIP / postal code of the leased premises."),
        new ExtractionFieldSpec("unit_number", "string",
            "Apartment / unit / suite identifier of the leased premises (e.g. '4B', 'Apt 2', '101'). Empty for a single-family home with no unit designation."),
        new ExtractionFieldSpec("unit_bedrooms", "number",
            "Number of bedrooms in the leased unit, as a decimal (e.g. 2 or 1.5), only if stated on the lease."),
        new ExtractionFieldSpec("unit_bathrooms", "number",
            "Number of bathrooms in the leased unit, as a decimal (e.g. 1 or 1.5), only if stated on the lease."),
        new ExtractionFieldSpec("unit_square_feet", "integer",
            "Approximate square footage of the leased unit, only if stated on the lease."),
        new ExtractionFieldSpec("lease_number", "string",
            "Lease, agreement, or contract number as printed on the document, if any."),
        new ExtractionFieldSpec("start_date", "date",
            "Lease start / commencement date in ISO 8601 (YYYY-MM-DD).", Required: true),
        new ExtractionFieldSpec("end_date", "date",
            "Lease end / expiration date in ISO 8601 (YYYY-MM-DD).", Required: true),
        new ExtractionFieldSpec("monthly_rent", "number",
            "Monthly rent amount, as a decimal number (no currency symbol).", Required: true),
        new ExtractionFieldSpec("security_deposit", "number",
            "Security deposit amount, as a decimal number (no currency symbol)."),
        new ExtractionFieldSpec("late_fee", "number",
            "Late-fee amount charged for an overdue rent payment, as a decimal number (no currency symbol)."),
        new ExtractionFieldSpec("rent_due_day", "integer",
            "Day of the month rent is due (1–31). Default to 1 if the lease only says 'first of the month'."),
    };
}
