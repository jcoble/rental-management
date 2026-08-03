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
    public IReadOnlyList<MonthlyCashFlow> Months { get; set; } = [];

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

    /// <summary>Actual cash received and allocated to non-deposit charges.</summary>
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
    public IReadOnlyList<CashFlowDetailRow> OperatingExpenseDetails { get; set; } = [];
    public IReadOnlyList<CashFlowDetailRow> DebtServiceDetails { get; set; } = [];
}

public sealed class CashFlowDetailRow
{
    public string Label { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed class MonthlyCashFlow
{
    public string Month { get; set; } = string.Empty;
    public decimal Income { get; set; }
    public decimal OperatingExpenses { get; set; }
    public decimal DebtService { get; set; }
    public decimal CashFlow { get; set; }
}
