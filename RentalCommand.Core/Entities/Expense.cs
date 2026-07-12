using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class Expense : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? VendorId { get; set; }
    public int? WorkOrderId { get; set; }
    public int? CapitalizedAssetId { get; set; }
    public int? RecurringExpenseId { get; set; }
    public DateTime? RecurringExpenseOccurrenceDate { get; set; }
    public ScheduleECategory Category { get; set; } = ScheduleECategory.Other;
    public string Description { get; set; } = string.Empty;
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Pending;
    public decimal Amount { get; set; }
    public DateTime IncurredAt { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool BillableToOwner { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Subtotal before tax, tip, and other charges; populated from scanned receipts.</summary>
    public decimal? Subtotal { get; set; }

    /// <summary>Tax amount charged; populated from scanned receipts.</summary>
    public decimal? TaxAmount { get; set; }

    /// <summary>
    /// JSON object (stored as jsonb) holding the full receipt details that did not promote to
    /// first-class columns: vendor contact, receipt number, payment info, tax rate, tip, discount,
    /// shipping, line items, and any extra extracted fields.
    /// </summary>
    public string? ReceiptData { get; set; }

    /// <summary>How the expense was paid (e.g. "Visa", "Cash"); promoted from the scanned receipt.</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>Last 4 digits of the card used, when the receipt showed it; promoted from the scan.</summary>
    public string? CardLast4 { get; set; }

    /// <summary>Document classification from the scan (e.g. "Receipt", "Bill", "Invoice", "UtilityBill").</summary>
    public string? DocumentKind { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Vendor? Vendor { get; set; }
    public WorkOrder? WorkOrder { get; set; }
    public CapitalAsset? CapitalizedAsset { get; set; }
    public RecurringExpense? RecurringExpense { get; set; }

    /// <summary>Itemized lines from the scanned receipt, promoted to a queryable child table.</summary>
    public ICollection<ExpenseLineItem> LineItems { get; set; } = new List<ExpenseLineItem>();
}
