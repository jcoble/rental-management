using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

public static class ReceiptExtractionSchema
{
    public const string PromptId = "receipt-to-expense-v1";

    public const string Instructions =
        "You are extracting fields from a vendor receipt or invoice for a US residential-rental " +
        "bookkeeping system. Read the document and fill every field you can. " +
        "For 'category', map the expense to the closest IRS Schedule E category. " +
        "For 'line_items', return one object per line item on the receipt (description, quantity, unit_price, amount). " +
        "Leave unknown or absent fields empty — never invent values not present on the document.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } = new[]
    {
        new ExtractionFieldSpec("vendor_name",    "string", "The merchant / vendor / payee name.", Required: true),
        new ExtractionFieldSpec("vendor_address", "string", "Full street address of the vendor."),
        new ExtractionFieldSpec("vendor_phone",   "string", "Vendor phone number as printed."),
        new ExtractionFieldSpec("vendor_website", "string", "Vendor website URL as printed."),
        new ExtractionFieldSpec("vendor_tax_id",  "string", "Vendor EIN or tax ID number."),
        new ExtractionFieldSpec("receipt_number", "string", "Receipt, invoice, or transaction number."),
        new ExtractionFieldSpec("transaction_date", "date",
            "Date of the transaction in ISO 8601 (YYYY-MM-DD).", Required: true),
        new ExtractionFieldSpec("subtotal", "number",
            "Subtotal before tax/tip/discount, as a decimal number (no currency symbol)."),
        new ExtractionFieldSpec("tax",      "number",
            "Tax amount charged, as a decimal number (no currency symbol)."),
        new ExtractionFieldSpec("tax_rate", "number",
            "Tax rate as a decimal fraction, e.g. 0.085 for 8.5%. Leave empty if not shown."),
        new ExtractionFieldSpec("tip",      "number",
            "Tip or gratuity amount, as a decimal number (no currency symbol)."),
        new ExtractionFieldSpec("discount", "number",
            "Total discount applied, as a decimal number (positive value, no currency symbol)."),
        new ExtractionFieldSpec("shipping", "number",
            "Shipping or delivery charge, as a decimal number (no currency symbol)."),
        new ExtractionFieldSpec("total",    "number",
            "Grand total charged, as a decimal number (no currency symbol).", Required: true),
        new ExtractionFieldSpec("payment_method", "string",
            "Payment method (e.g. Visa, Cash, Check, ACH)."),
        new ExtractionFieldSpec("card_last4", "string",
            "Last 4 digits of the payment card, if shown."),
        new ExtractionFieldSpec("category", "enum",
            "Closest IRS Schedule E expense category.", Required: true,
            EnumValues: Enum.GetNames<RentalCommand.Core.Enums.ScheduleECategory>()),
        new ExtractionFieldSpec("notes", "string",
            "Any short free-text note (e.g. purpose, job reference). Optional."),
        new ExtractionFieldSpec("line_items", "array",
            "One object per line item on the receipt.",
            ItemFields: new[]
            {
                new ExtractionFieldSpec("description", "string", "Line-item description or product name."),
                new ExtractionFieldSpec("quantity",    "number", "Quantity purchased (decimal)."),
                new ExtractionFieldSpec("unit_price",  "number", "Price per unit (decimal, no currency symbol)."),
                new ExtractionFieldSpec("amount",      "number", "Total for this line (decimal, no currency symbol)."),
            }),
    };
}
