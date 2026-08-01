using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Money;

internal sealed class MoneyExpenseAllocationPayload
{
    public ExpenseAllocationTargetKind TargetKind { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? OwnerEntityId { get; set; }
    public decimal Amount { get; set; }
}

internal sealed class MoneyExpenseLinePayload
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Amount { get; set; }
    public int LineNumber { get; set; }
}

internal sealed class MoneyCreateExpensePayload
{
    public ExpenseOperationalScope? OperationalScope { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? VendorId { get; set; }
    public int? WorkOrderId { get; set; }
    public ScheduleECategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public ExpenseStatus Status { get; set; }
    public decimal Amount { get; set; }
    public DateTime IncurredAt { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool BillableToOwner { get; set; }
    public string? Notes { get; set; }
    public decimal? Subtotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public string? ReceiptData { get; set; }
    public string? PaymentMethod { get; set; }
    public string? CardLast4 { get; set; }
    public string? DocumentKind { get; set; }
    public List<MoneyExpenseLinePayload> LineItems { get; set; } = [];
    public List<MoneyExpenseAllocationPayload> Allocations { get; set; } = [];
}

internal sealed class MoneyUpdateExpensePayload
{
    public ExpenseOperationalScope? OperationalScope { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? VendorId { get; set; }
    public int? WorkOrderId { get; set; }
    public ScheduleECategory? Category { get; set; }
    public string? Description { get; set; }
    public ExpenseStatus? Status { get; set; }
    public decimal? Amount { get; set; }
    public DateTime? IncurredAt { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool? BillableToOwner { get; set; }
    public string? Notes { get; set; }
    public decimal? Subtotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public string? ReceiptData { get; set; }
    public bool? ClearReceiptData { get; set; }
    public List<MoneyExpenseLinePayload>? LineItems { get; set; }
    public List<MoneyExpenseAllocationPayload>? Allocations { get; set; }
}

internal sealed class MoneyCreateRecurringExpensePayload
{
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public ScheduleECategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public RecurringExpenseFrequency Frequency { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? NextRunDate { get; set; }
    public bool Active { get; set; }
    public string? Notes { get; set; }
}

internal sealed class MoneyUpdateRecurringExpensePayload
{
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public ScheduleECategory? Category { get; set; }
    public string? Description { get; set; }
    public decimal? Amount { get; set; }
    public RecurringExpenseFrequency? Frequency { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? NextRunDate { get; set; }
    public bool? Active { get; set; }
    public string? Notes { get; set; }
}

internal sealed class MoneyCreateLoanPayload
{
    public int PropertyId { get; set; }
    public string Lender { get; set; } = string.Empty;
    public decimal OriginalAmount { get; set; }
    public decimal? CurrentBalance { get; set; }
    public decimal AnnualInterestRatePct { get; set; }
    public int TermMonths { get; set; }
    public DateTime StartDate { get; set; }
    public int DayOfMonthDue { get; set; }
    public decimal MonthlyPrincipalInterest { get; set; }
    public decimal MonthlyEscrow { get; set; }
    public bool EscrowCoversTaxes { get; set; }
    public bool EscrowCoversInsurance { get; set; }
    public LoanStatus Status { get; set; }
    public string? Notes { get; set; }
}

internal sealed class MoneyUpdateLoanPayload
{
    public string? Lender { get; set; }
    public decimal? OriginalAmount { get; set; }
    public decimal? CurrentBalance { get; set; }
    public decimal? AnnualInterestRatePct { get; set; }
    public int? TermMonths { get; set; }
    public DateTime? StartDate { get; set; }
    public int? DayOfMonthDue { get; set; }
    public decimal? MonthlyPrincipalInterest { get; set; }
    public decimal? MonthlyEscrow { get; set; }
    public bool? EscrowCoversTaxes { get; set; }
    public bool? EscrowCoversInsurance { get; set; }
    public LoanStatus? Status { get; set; }
    public string? Notes { get; set; }
}

internal sealed class MoneyPostLoanPaymentPayload
{
    public int LoanId { get; set; }
    public DateTime? PaidDate { get; set; }
}

internal sealed class MoneyCapitalizeExpensePayload
{
    public DateTime InServiceDate { get; set; }
    public DepreciationMethod Method { get; set; }
    public decimal RecoveryYears { get; set; }
    public DepreciationConvention Convention { get; set; }
    public string? Description { get; set; }
}

internal sealed class MoneyCreateCapitalAssetPayload
{
    public int PropertyId { get; set; }
    public int? UnitId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal CostBasis { get; set; }
    public DateTime InServiceDate { get; set; }
    public DepreciationMethod Method { get; set; }
    public decimal RecoveryYears { get; set; }
    public DepreciationConvention Convention { get; set; }
    public decimal AccumulatedDepreciation { get; set; }
    public DateTime? DisposedOnDate { get; set; }
}

internal sealed class MoneyUpdateCapitalAssetPayload
{
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public bool? ClearUnit { get; set; }
    public string? Description { get; set; }
    public decimal? CostBasis { get; set; }
    public DateTime? InServiceDate { get; set; }
    public DepreciationMethod? Method { get; set; }
    public decimal? RecoveryYears { get; set; }
    public DepreciationConvention? Convention { get; set; }
    public decimal? AccumulatedDepreciation { get; set; }
    public DateTime? DisposedOnDate { get; set; }
    public bool? ClearDisposedOnDate { get; set; }
}

internal sealed class MoneyUpdatePropertyDispositionPayload
{
    public DateTime? ClosedOnDate { get; set; }
    public decimal? SalePrice { get; set; }
    public decimal? SellingCosts { get; set; }
    public string? BuyerName { get; set; }
    public string? Memo { get; set; }
}

internal sealed class MoneyCreateOwnerDistributionPayload
{
    public int OwnerEntityId { get; set; }
    public int? PropertyId { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public DistributionMethod Method { get; set; }
    public string? Memo { get; set; }
}

internal sealed class MoneyUpdateOwnerDistributionPayload
{
    public int? OwnerEntityId { get; set; }
    public int? PropertyId { get; set; }
    public bool? ClearProperty { get; set; }
    public DateTime? Date { get; set; }
    public decimal? Amount { get; set; }
    public DistributionMethod? Method { get; set; }
    public string? Memo { get; set; }
}

internal sealed class MoneyApproveOwnerDistributionPayload
{
    public string BankReference { get; set; } = string.Empty;
    public string ExportReference { get; set; } = string.Empty;
    public DateTime? ExportedAt { get; set; }
}

internal sealed class MoneyRejectOwnerDistributionPayload
{
    public string? Reason { get; set; }
}

internal sealed class MoneyCreateOwnerContributionPayload
{
    public int OwnerEntityId { get; set; }
    public int? PropertyId { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public DistributionMethod Method { get; set; }
    public string? Memo { get; set; }
}

internal sealed class MoneyUpdateOwnerContributionPayload
{
    public int? OwnerEntityId { get; set; }
    public int? PropertyId { get; set; }
    public bool? ClearProperty { get; set; }
    public DateTime? Date { get; set; }
    public decimal? Amount { get; set; }
    public DistributionMethod? Method { get; set; }
    public string? Memo { get; set; }
}

internal sealed class MoneyApproveOwnerContributionPayload
{
    public string BankReference { get; set; } = string.Empty;
    public string ExportReference { get; set; } = string.Empty;
    public DateTime? ExportedAt { get; set; }
}

internal sealed class MoneyRejectOwnerContributionPayload
{
    public string? Reason { get; set; }
}
