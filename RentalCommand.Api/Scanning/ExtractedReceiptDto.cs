namespace RentalCommand.Api.Scanning;

/// <summary>
/// Represents a single line item extracted from a receipt.
/// </summary>
public sealed record ReceiptLineItem(
    string? Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? Amount);

/// <summary>
/// Rich DTO produced by <see cref="ScanService.BuildReceiptDto"/> from the worker-written
/// extraction JSON. Covers all fields in <see cref="ReceiptExtractionSchema.Fields"/>.
/// </summary>
public sealed class ExtractedReceiptDto
{
    // ---- Vendor ----
    public string? VendorName    { get; set; }
    public string? VendorAddress { get; set; }
    public string? VendorPhone   { get; set; }
    public string? VendorWebsite { get; set; }
    public string? VendorTaxId   { get; set; }

    // ---- Receipt header ----
    public string?   ReceiptNumber   { get; set; }
    public DateTime? TransactionDate { get; set; }

    // ---- Money breakdown ----
    public decimal? Subtotal { get; set; }
    public decimal? Tax      { get; set; }
    public decimal? TaxRate  { get; set; }
    public decimal? Tip      { get; set; }
    public decimal? Discount { get; set; }
    public decimal? Shipping { get; set; }
    public decimal? Total    { get; set; }

    // ---- Payment ----
    public string? PaymentMethod { get; set; }
    public string? CardLast4     { get; set; }

    // ---- Classification ----
    public RentalCommand.Core.Enums.ScheduleECategory? Category { get; set; }
    public string? Notes { get; set; }

    // ---- Line items ----
    public List<ReceiptLineItem> LineItems { get; set; } = new();

    // ---- Catch-all for any extra fields not in the schema ----
    public Dictionary<string, string> Extra { get; set; } = new();
}
