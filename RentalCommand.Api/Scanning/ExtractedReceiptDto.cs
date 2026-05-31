namespace RentalCommand.Api.Scanning;

public sealed class ExtractedReceiptDto
{
    public string? VendorName { get; set; }
    public decimal? Amount { get; set; }
    public DateTime? TransactionDate { get; set; }
    public RentalCommand.Core.Enums.ScheduleECategory? Category { get; set; }
    public string? Notes { get; set; }
}
