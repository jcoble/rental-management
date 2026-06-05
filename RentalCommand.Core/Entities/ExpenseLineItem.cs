namespace RentalCommand.Core.Entities;

/// <summary>
/// One line item belonging to an <see cref="Expense"/>, promoted out of the scanned receipt's
/// JSON so individual lines are queryable and reportable (e.g. spend by description). Created
/// when a scan draft is confirmed, and backfilled from existing <see cref="Expense.ReceiptData"/>
/// line-item arrays by the ScanExtractionTypedFields migration.
/// </summary>
public class ExpenseLineItem
{
    public int Id { get; set; }

    /// <summary>Owning expense; the row is cascade-deleted with its expense.</summary>
    public int ExpenseId { get; set; }

    /// <summary>Free-text description of the line (may be empty when the receipt omitted one).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Quantity, when the receipt itemized one.</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Per-unit price, when the receipt itemized one.</summary>
    public decimal? UnitPrice { get; set; }

    /// <summary>Line total, when the receipt itemized one.</summary>
    public decimal? Amount { get; set; }

    /// <summary>1-based position of this line within the receipt (preserves the printed order).</summary>
    public int LineNumber { get; set; }

    public Expense? Expense { get; set; }
}
