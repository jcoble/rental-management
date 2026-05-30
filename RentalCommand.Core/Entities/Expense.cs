using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class Expense
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public int? VendorId { get; set; }
    public int? WorkOrderId { get; set; }
    public string Category { get; set; } = string.Empty;
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

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Vendor? Vendor { get; set; }
    public WorkOrder? WorkOrder { get; set; }
}
