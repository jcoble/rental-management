using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Extraction schema for the public rental-application autofill: an applicant snaps a photo of a
/// government ID (driver's license / passport / state ID) or a pay stub, and the LLM returns
/// prefill fields with confidences. The full ID number is intentionally NOT in the schema — only a
/// non-identifying last-4 hint — so a complete ID number is never extracted or persisted.
/// </summary>
public static class IdExtractionSchema
{
    public const string PromptId = "photo-id-to-application-v1";

    public const string Instructions =
        "You are extracting fields from a photo of a US applicant's government-issued ID " +
        "(driver's license, state ID, or passport) or a recent pay stub, to pre-fill an online " +
        "rental application. Read the document and fill every field you can. " +
        "Return dates in ISO 8601 (YYYY-MM-DD). " +
        "For 'id_last4', return ONLY the last four characters of the ID/license/document number — " +
        "never the full number. " +
        "For 'monthly_income', only fill it from a pay stub: report the gross monthly income as a " +
        "decimal number (no currency symbol); if you only see a per-pay-period amount, convert it to " +
        "a monthly figure when the pay frequency is shown, otherwise leave it empty. " +
        "Leave unknown or absent fields empty — never invent values not present on the document.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } = new[]
    {
        new ExtractionFieldSpec("first_name", "string", "The applicant's first (given) name as printed."),
        new ExtractionFieldSpec("last_name", "string", "The applicant's last (family) name as printed."),
        new ExtractionFieldSpec("date_of_birth", "date",
            "Date of birth in ISO 8601 (YYYY-MM-DD)."),
        new ExtractionFieldSpec("address", "string",
            "Full residential / mailing address as printed on the ID."),
        new ExtractionFieldSpec("id_last4", "string",
            "ONLY the last four characters of the ID/license/document number. Never return the full number."),
        new ExtractionFieldSpec("employer", "string",
            "Employer / company name. Only present on a pay stub."),
        new ExtractionFieldSpec("monthly_income", "number",
            "Gross monthly income as a decimal number (no currency symbol). Only from a pay stub."),
    };
}
