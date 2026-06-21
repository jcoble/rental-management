namespace RentalCommand.Api.DTOs;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// True cash-flow DTOs (spec §9 + §18). Distinct from the simple income-vs-expense CashFlowResponse:
// this models what actually hits the owner's pocket — rent in, operating expenses out, then full
// debt service out — and keeps depreciation (non-cash) OUT. Money is decimal; computed DB-side.

/// <summary>
/// Per-property + portfolio true cash flow for a period: operating performance (NOI) and after-debt
/// cash flow shown as distinct lines.
/// </summary>
public class CashFlowSummaryResponse
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }

    /// <summary>One row per property with activity, plus the per-property breakdown.</summary>
    public IReadOnlyList<PropertyCashFlow> Properties { get; set; } = [];

    // Portfolio totals (foot the per-property rows).
    public decimal TotalIncome { get; set; }
    public decimal TotalOperatingExpenses { get; set; }
    public decimal TotalNoi { get; set; }
    public decimal TotalDebtService { get; set; }
    public decimal TotalCashFlow { get; set; }
}

/// <summary>One property's cash flow for the period.</summary>
public class PropertyCashFlow
{
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>Actual cash received: Rent + LateFee, Paid (full) or Partial (collected), no deposits.</summary>
    public decimal Income { get; set; }

    /// <summary>
    /// Operating expenses that are real cash out — escrow-funded Taxes/Insurance are excluded here
    /// (their cash is inside debt service), category-specifically per the property's loan.
    /// </summary>
    public decimal OperatingExpenses { get; set; }

    /// <summary>Net operating income = income − operating expenses (before debt service).</summary>
    public decimal Noi { get; set; }

    /// <summary>Full debt service for the period: Σ LoanPayment.TotalAmount (principal + interest + escrow).</summary>
    public decimal DebtService { get; set; }

    /// <summary>What actually hits the pocket = NOI − debt service. Excludes non-cash depreciation.</summary>
    public decimal CashFlow { get; set; }
}
