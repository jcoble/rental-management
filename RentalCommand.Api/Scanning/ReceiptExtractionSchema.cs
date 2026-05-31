using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

public static class ReceiptExtractionSchema
{
    public const string PromptId = "receipt-to-expense-v1";

    public const string Instructions =
        "You are extracting fields from a vendor receipt or invoice for a US residential-rental " +
        "bookkeeping system. Read the document and fill every field you can. For 'category', map the " +
        "expense to the closest IRS Schedule E category. Do NOT guess values that are not present.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } = new[]
    {
        new ExtractionFieldSpec("vendor_name", "string", "The merchant / vendor / payee name.", Required: true),
        new ExtractionFieldSpec("amount", "number", "The grand total charged, as a decimal number (no currency symbol).", Required: true),
        new ExtractionFieldSpec("transaction_date", "date", "Date of the transaction in ISO 8601 (YYYY-MM-DD).", Required: true),
        new ExtractionFieldSpec("category", "enum", "Closest IRS Schedule E expense category.", Required: true,
            EnumValues: Enum.GetNames<RentalCommand.Core.Enums.ScheduleECategory>()),
        new ExtractionFieldSpec("notes", "string", "Any short free-text note (e.g. line-item summary). Optional."),
    };
}
