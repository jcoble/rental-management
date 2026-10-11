using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data;

public sealed class LoanPaymentEffectiveRow
{
    public int Id { get; init; }
    public int PortfolioId { get; init; }
    public int LoanId { get; init; }
    public int PropertyId { get; init; }
    public string PeriodKey { get; init; } = string.Empty;
    public DateTime DueDate { get; init; }
    public DateTime? PaidDate { get; init; }
    public decimal InterestAmount { get; init; }
    public decimal PrincipalAmount { get; init; }
    public decimal EscrowAmount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal BalanceAfter { get; init; }
    public LoanPaymentStatus Status { get; init; }
    public bool PaymentDoesNotCoverInterest { get; init; }
    public int? CorrectionId { get; init; }
}

/// <summary>
/// Sole composable owner of the effective loan-payment projection. EF translates latest-correction
/// selection and all caller composition into the caller's single SQL statement.
/// </summary>
public static class LoanPaymentEffectiveQuery
{
    public static IQueryable<LoanPaymentEffectiveRow> From(RentalCommandDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        return
            from payment in db.LoanPayments.AsNoTracking()
            join loan in db.Loans.AsNoTracking()
                on new { payment.LoanId, payment.PortfolioId }
                equals new { LoanId = loan.Id, loan.PortfolioId }
            from correction in db.LoanPaymentCorrections.AsNoTracking()
                .Where(candidate =>
                    candidate.LoanPaymentId == payment.Id
                    && candidate.PortfolioId == payment.PortfolioId)
                .OrderByDescending(candidate => candidate.Id)
                .Take(1)
                .DefaultIfEmpty()
            select new LoanPaymentEffectiveRow
            {
                Id = payment.Id,
                PortfolioId = payment.PortfolioId,
                LoanId = payment.LoanId,
                PropertyId = loan.PropertyId,
                PeriodKey = payment.PeriodKey,
                DueDate = correction == null ? payment.DueDate : correction.DueDate,
                PaidDate = correction == null ? payment.PaidDate : correction.PaidDate,
                InterestAmount = correction == null ? payment.InterestAmount : correction.InterestAmount,
                PrincipalAmount = correction == null ? payment.PrincipalAmount : correction.PrincipalAmount,
                EscrowAmount = correction == null ? payment.EscrowAmount : correction.EscrowAmount,
                TotalAmount = correction == null ? payment.TotalAmount : correction.TotalAmount,
                BalanceAfter = correction == null ? payment.BalanceAfter : correction.BalanceAfter,
                Status = correction == null ? payment.Status : correction.Status,
                PaymentDoesNotCoverInterest = correction == null
                    ? payment.PaymentDoesNotCoverInterest
                    : correction.PaymentDoesNotCoverInterest,
                CorrectionId = correction == null ? null : correction.Id,
            };
    }
}
