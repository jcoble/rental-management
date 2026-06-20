using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class Payment : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public PaymentType PaymentType { get; set; } = PaymentType.Rent;
    public PaymentStatus Status { get; set; } = PaymentStatus.Scheduled;
    public decimal Amount { get; set; }

    /// <summary>
    /// Cash actually collected against this charge, when it differs from <see cref="Amount"/>. Only
    /// meaningful for a <see cref="PaymentStatus.Partial"/> payment, where it is the amount paid so far
    /// (strictly between 0 and <see cref="Amount"/>); the unpaid remainder (<c>Amount − AmountPaid</c>)
    /// is what is still owed. A <see cref="PaymentStatus.Paid"/> payment leaves this null and is treated
    /// as fully collected (the whole <see cref="Amount"/>); Scheduled/Late/Waived/etc. leave it null.
    /// Receivables/collected aggregations read it so a Partial row contributes its real split.
    /// </summary>
    public decimal? AmountPaid { get; set; }

    public DateTime DueDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public string? Method { get; set; }
    public string? ExternalReference { get; set; }
    public string? Notes { get; set; }
    /// <summary>Billing period ("yyyy-MM") for auto-generated rent/late-fee rows; null for manual/one-off payments. Used for idempotency.</summary>
    public string? PeriodKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Name on the check / of the payer; promoted from a scanned rent check.</summary>
    public string? PayerName { get; set; }

    /// <summary>Check number; promoted from a scanned rent check.</summary>
    public string? CheckNumber { get; set; }

    /// <summary>Issuing bank name; promoted from a scanned rent check.</summary>
    public string? BankName { get; set; }

    /// <summary>
    /// JSON object (stored as jsonb) holding the full scan extraction superset for payments created
    /// from a scan draft. Null for manually entered or auto-generated payments.
    /// </summary>
    public string? ExtractedData { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Lease? Lease { get; set; }
}
