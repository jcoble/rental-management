using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Extraction schema for importing a mortgage statement or closing disclosure into a Loan draft — the
/// "scan your mortgage" counterpart of the manual loan form on a property. A landlord uploads a monthly
/// mortgage statement (or the closing disclosure from origination) and the confirm path maps the
/// extracted terms onto a per-property <see cref="Core.Entities.Loan"/>; once Active, the Engine's
/// debt-service worker generates the amortization schedule.
///
/// Mirrors <see cref="LeaseExtractionSchema"/>: <c>property_id</c> is copied VERBATIM from the grounded
/// {id,name} list and re-validated in-portfolio before it is ever trusted, so a hallucinated or foreign
/// id can never link another portfolio's row (IDOR). In practice the property is supplied by the scan
/// context (the landlord launches the scan from a specific property), so the id is a best-effort hint;
/// the reviewer's selection wins. Everything else is read straight off the statement.
/// </summary>
public static class LoanExtractionSchema
{
    public const string PromptId = "mortgage-statement-to-loan-v1";

    public const string Instructions =
        "You are extracting the key terms of a US residential MORTGAGE from a monthly mortgage statement " +
        "or a closing disclosure (the origination settlement statement), for a property-management system, " +
        "so the loan can be imported as a record. Read the document and fill every field you can. " +
        "lender is the name of the lender / servicer / bank that holds the mortgage (e.g. 'Rocket Mortgage', " +
        "'Wells Fargo Home Mortgage'). " +
        "original_amount is the ORIGINAL loan amount at origination (the closing disclosure's 'Loan Amount', " +
        "or a statement's 'original principal'); current_balance is the CURRENT outstanding principal balance " +
        "(a monthly statement's 'principal balance' / 'outstanding principal' / 'unpaid principal balance'). " +
        "Both are decimal numbers with no currency symbol. " +
        "annual_interest_rate_pct is the annual interest rate as a PERCENT number, e.g. 6.5 for 6.5% (never a " +
        "fraction like 0.065). " +
        "term_months is the amortization term in MONTHS: if the document states the term in years (e.g. a 30-year " +
        "loan), multiply by 12 (30 → 360). " +
        "start_date is the loan's origination / first-payment date in ISO 8601 (YYYY-MM-DD); use the closing/" +
        "disbursement date on a closing disclosure, or the first-payment date on a statement. " +
        "day_of_month_due is the day of the month the payment is due (1–31). " +
        "monthly_principal_interest is the scheduled principal + interest portion of the monthly payment (the " +
        "'P&I'); monthly_escrow is the monthly escrow portion for taxes/insurance (0 if the loan does not escrow). " +
        "Both are decimal numbers with no currency symbol. Do NOT put the full monthly payment in P&I when escrow " +
        "is itemized separately — split them. " +
        "escrow_covers_taxes is true when the escrow account pays the property taxes; escrow_covers_insurance is " +
        "true when it pays the hazard/homeowner's insurance. Return the literal string 'true' or 'false'. " +
        "The grounding context lists this portfolio's known properties, each with its exact numeric id. For " +
        "property_id, return the matching id COPIED VERBATIM from that grounding list, and only when the " +
        "mortgaged property's address strongly matches; otherwise leave it empty. Never return an id that is not " +
        "present in the grounding list, and never guess or fabricate an id. " +
        "property_address is the street address of the mortgaged property as written on the document. " +
        "Leave unknown or absent fields empty — never invent values not present on the document.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } = new[]
    {
        new ExtractionFieldSpec("target_entity_type", "enum",
            "Always set to Loan for this schema.",
            EnumValues: new[] { "Loan" }),
        new ExtractionFieldSpec("lender", "string",
            "Name of the lender / servicer / bank that holds the mortgage, as written on the document.",
            Required: true),
        new ExtractionFieldSpec("original_amount", "number",
            "Original loan amount at origination (closing disclosure 'Loan Amount' or original principal), as a decimal number with no currency symbol."),
        new ExtractionFieldSpec("current_balance", "number",
            "Current outstanding principal balance (statement 'principal balance' / 'unpaid principal balance'), as a decimal number. Leave empty on a closing disclosure where only the original amount is shown."),
        new ExtractionFieldSpec("annual_interest_rate_pct", "number",
            "Annual interest rate as a PERCENT number, e.g. 6.5 for 6.5% (never a fraction like 0.065)."),
        new ExtractionFieldSpec("term_months", "integer",
            "Amortization term in MONTHS (a 30-year loan is 360). Convert years to months by multiplying by 12."),
        new ExtractionFieldSpec("start_date", "date",
            "Loan origination / first-payment date in ISO 8601 (YYYY-MM-DD)."),
        new ExtractionFieldSpec("day_of_month_due", "integer",
            "Day of the month the payment is due (1–31). Default to 1 if the document only says 'first of the month'."),
        new ExtractionFieldSpec("monthly_principal_interest", "number",
            "Scheduled principal + interest portion of the monthly payment (the 'P&I'), as a decimal number with no currency symbol."),
        new ExtractionFieldSpec("monthly_escrow", "number",
            "Monthly escrow portion of the payment for taxes/insurance, as a decimal number. Use 0 if the loan does not escrow."),
        new ExtractionFieldSpec("escrow_covers_taxes", "enum",
            "Whether the escrow account pays the property taxes. Return 'true' or 'false'.",
            EnumValues: new[] { "true", "false" }),
        new ExtractionFieldSpec("escrow_covers_insurance", "enum",
            "Whether the escrow account pays the hazard / homeowner's insurance. Return 'true' or 'false'.",
            EnumValues: new[] { "true", "false" }),
        new ExtractionFieldSpec("property_id", "integer",
            "Exact id of the matching property, copied from the grounding list's properties[].id, only when the mortgaged address strongly matches. Empty otherwise."),
        new ExtractionFieldSpec("property_address", "string",
            "Street address of the mortgaged property as written on the document."),
        new ExtractionFieldSpec("notes", "string",
            "Any short free-text note worth keeping (e.g. loan number, PMI note). Optional."),
    };
}
