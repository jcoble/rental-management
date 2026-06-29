using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Extraction schema for importing a COMPLETED paper rental application into an Application draft —
/// the scan-IN counterpart of the public apply form. A landlord is handed a filled-out application
/// and scans it; the confirm path maps the extracted fields onto a <see cref="Core.Entities.RentalApplication"/>
/// (the same entity the public <c>/apply/{token}</c> form creates), so the applicant lands on /applications.
///
/// This mirrors <see cref="LeaseExtractionSchema"/>: <c>property_id</c>/<c>unit_id</c> are copied VERBATIM
/// from the grounded {id,name} lists and re-validated in-portfolio before they are ever trusted, so a
/// hallucinated or foreign id can never link another portfolio's row (IDOR). Everything else is free text
/// read straight off the paper application. There is intentionally NO full-ID-number field — only a
/// non-identifying <c>id_last4</c> hint — so a complete government-ID number is never extracted or persisted.
/// </summary>
public static class ApplicationExtractionSchema
{
    public const string PromptId = "rental-application-pdf-to-application-v1";

    public const string Instructions =
        "You are extracting the fields of a COMPLETED US residential rental application " +
        "(a filled-out application form, scanned as a PDF or photo) for a property-management system, " +
        "so the applicant can be imported as a record. Read the document and fill every field you can. " +
        "first_name and last_name are the primary applicant's given and family names exactly as written. " +
        "email and phone are the applicant's contact details. " +
        "date_of_birth must be ISO 8601 (YYYY-MM-DD). " +
        "current_address is the applicant's present home / mailing address as a single line " +
        "(street, city, state, ZIP) exactly as written. " +
        "employer is the applicant's employer / company name; monthly_income is their stated gross monthly " +
        "income as a decimal number with no currency symbol (if only an annual figure is given, divide by 12). " +
        "applying_for is the property / unit the applicant says they are applying for, as free text " +
        "(e.g. 'Lakecrest Condos - Unit 304'); do NOT invent an id for it. " +
        "id_last4 is ONLY the last four characters of any government-ID / driver's-license number printed " +
        "on the application — never the full number; leave it empty if none is shown. " +
        "co_signer_name is the full name of a co-signer / guarantor if the application has a co-signer block, " +
        "else leave it empty. " +
        "desired_move_in_date is the requested move-in / occupancy date in ISO 8601 (YYYY-MM-DD), if given. " +
        "The grounding context lists this portfolio's known properties and units, each with its exact numeric id. " +
        "For property_id and unit_id, return the matching id COPIED VERBATIM from that grounding list, and only " +
        "when the applied-for property/unit strongly matches; otherwise leave the id empty. " +
        "Never return an id that is not present in the grounding list, and never guess or fabricate an id. " +
        "Leave unknown or absent fields empty — never invent values not present on the document.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } = new[]
    {
        // No target_entity_type field: the draft's TargetEntityType is fixed from the upload-time
        // choice (and re-derived by ScanProcessingWorker), never from extraction. Emitting it only
        // added a stray conf-1.0 meta field to the review set, so it is intentionally omitted here.
        new ExtractionFieldSpec("first_name", "string",
            "The primary applicant's first (given) name as written on the application.",
            Required: true),
        new ExtractionFieldSpec("last_name", "string",
            "The primary applicant's last (family) name as written on the application.",
            Required: true),
        new ExtractionFieldSpec("email", "string",
            "The applicant's email address, if given."),
        new ExtractionFieldSpec("phone", "string",
            "The applicant's phone number, if given."),
        new ExtractionFieldSpec("date_of_birth", "date",
            "The applicant's date of birth in ISO 8601 (YYYY-MM-DD), if given."),
        new ExtractionFieldSpec("current_address", "string",
            "The applicant's current home / mailing address as a single line (street, city, state, ZIP)."),
        new ExtractionFieldSpec("employer", "string",
            "The applicant's employer / company name, if given."),
        new ExtractionFieldSpec("monthly_income", "number",
            "The applicant's stated gross monthly income as a decimal number with no currency symbol."),
        new ExtractionFieldSpec("applying_for", "string",
            "The property / unit the applicant is applying for, as free text (e.g. 'Lakecrest Unit 304'). Not an id."),
        new ExtractionFieldSpec("desired_move_in_date", "date",
            "The applicant's requested move-in date in ISO 8601 (YYYY-MM-DD), if given."),
        new ExtractionFieldSpec("id_last4", "string",
            "ONLY the last four characters of any government-ID / driver's-license number on the application. Never the full number."),
        new ExtractionFieldSpec("co_signer_name", "string",
            "Full name of a co-signer / guarantor if the application has a co-signer block; empty otherwise."),
        new ExtractionFieldSpec("property_id", "integer",
            "Exact id of the matching property, copied from the grounding list's properties[].id, only when strongly matched. Empty otherwise."),
        new ExtractionFieldSpec("unit_id", "integer",
            "Exact id of the matching unit, copied from the grounding list's units[].id, only when strongly matched. Empty otherwise."),
    };
}
