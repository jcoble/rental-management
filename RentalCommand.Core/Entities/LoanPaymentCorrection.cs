using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Append-only effective snapshot for an immutable <see cref="LoanPayment"/>.
/// The original occurrence remains unchanged; readers select the latest correction when present.
/// </summary>
public sealed class LoanPaymentCorrection : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LoanPaymentId { get; set; }
    public Guid AttemptId { get; set; }
    public int? SourceScanDraftId { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public decimal InterestAmount { get; set; }
    public decimal PrincipalAmount { get; set; }
    public decimal EscrowAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal BalanceAfter { get; set; }
    public LoanPaymentStatus Status { get; set; }
    public bool PaymentDoesNotCoverInterest { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Portfolio? Portfolio { get; set; }
    public LoanPayment? LoanPayment { get; set; }
}
