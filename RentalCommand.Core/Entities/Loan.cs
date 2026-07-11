using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A per-property mortgage / loan. Debt service is modeled as its own thing (not an
/// <see cref="Expense"/>) so the principal/interest/escrow split is explicit: cash flow can use the
/// full payment while Schedule E deducts only the interest (principal is never deductible).
///
/// One loan per property (a property may have a paid-off loan plus none active). A blanket loan
/// spanning several properties is, for v1, entered as a single loan on a primary property.
///
/// <para><b>Balance is a derived cache (spec §18).</b> <see cref="CurrentBalance"/> is updated by the
/// <c>DebtServiceWorker</c> after each generated <see cref="LoanPayment"/>, but the authoritative,
/// immutable amortization comes from the <see cref="LoanPayment"/> rows themselves — each period's
/// opening balance is the prior payment's <see cref="LoanPayment.BalanceAfter"/>, never this live,
/// user-editable field. That keeps a filed interest figure from silently changing if the balance is
/// later edited.</para>
/// </summary>
public class Loan : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>The property this loan is secured against (validated in-portfolio on write).</summary>
    public int PropertyId { get; set; }

    public string Lender { get; set; } = string.Empty;

    /// <summary>Original principal at origination.</summary>
    public decimal OriginalAmount { get; set; }

    /// <summary>
    /// Live outstanding principal. <b>Derived cache</b> (spec §18): maintained by the debt-service
    /// worker from the amortization rows; do not treat as the source of truth for a period's split.
    /// </summary>
    public decimal CurrentBalance { get; set; }

    /// <summary>Annual nominal interest rate as a percent, e.g. <c>6.5</c> for 6.5%.</summary>
    public decimal AnnualInterestRatePct { get; set; }

    /// <summary>Amortization term in months (e.g. 360 for a 30-year loan). Drives the maturity stop.</summary>
    public int TermMonths { get; set; }

    /// <summary>Loan start / first-payment-period anchor (UTC). Period index is counted from here.</summary>
    public DateTime StartDate { get; set; }

    /// <summary>Day of month the payment is due (1–31, clamped to the month's length).</summary>
    public int DayOfMonthDue { get; set; } = 1;

    /// <summary>The scheduled principal + interest payment (the "P&amp;I"); escrow is separate.</summary>
    public decimal MonthlyPrincipalInterest { get; set; }

    /// <summary>Monthly escrow for taxes/insurance, 0 when the loan does not escrow.</summary>
    public decimal MonthlyEscrow { get; set; }

    /// <summary>True when the escrow funds property taxes (drives cash-flow escrow no-double-count).</summary>
    public bool EscrowCoversTaxes { get; set; }

    /// <summary>True when the escrow funds insurance (drives cash-flow escrow no-double-count).</summary>
    public bool EscrowCoversInsurance { get; set; }

    public LoanStatus Status { get; set; } = LoanStatus.Active;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Short-lived Engine ownership for debt-service generation.</summary>
    public string? WorkerClaimOwner { get; set; }
    public Guid? WorkerClaimToken { get; set; }
    public DateTime? WorkerClaimExpiresAtUtc { get; set; }
    public int WorkerClaimAttemptCount { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }

    /// <summary>The generated amortization schedule (one row per billing period).</summary>
    public List<LoanPayment> Payments { get; set; } = [];
}
