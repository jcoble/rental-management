using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Loan"/> (per-property mortgage).</summary>
public class LoanResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public string Lender { get; set; } = string.Empty;
    public decimal OriginalAmount { get; set; }
    public decimal CurrentBalance { get; set; }
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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string TestId => $"loan-{Id}";

    public static LoanResponse FromEntity(Loan e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        PropertyName = e.Property?.Name,
        Lender = e.Lender,
        OriginalAmount = e.OriginalAmount,
        CurrentBalance = e.CurrentBalance,
        AnnualInterestRatePct = e.AnnualInterestRatePct,
        TermMonths = e.TermMonths,
        StartDate = e.StartDate,
        DayOfMonthDue = e.DayOfMonthDue,
        MonthlyPrincipalInterest = e.MonthlyPrincipalInterest,
        MonthlyEscrow = e.MonthlyEscrow,
        EscrowCoversTaxes = e.EscrowCoversTaxes,
        EscrowCoversInsurance = e.EscrowCoversInsurance,
        Status = e.Status,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

/// <summary>One row of a loan's amortization schedule (read-only history).</summary>
public class LoanPaymentResponse
{
    public int Id { get; set; }
    public int LoanId { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public decimal InterestAmount { get; set; }
    public decimal PrincipalAmount { get; set; }
    public decimal EscrowAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal BalanceAfter { get; set; }
    public LoanPaymentStatus Status { get; set; }
    public bool PaymentDoesNotCoverInterest { get; set; }

    public static LoanPaymentResponse FromEntity(LoanPayment e) => new()
    {
        Id = e.Id,
        LoanId = e.LoanId,
        PeriodKey = e.PeriodKey,
        DueDate = e.DueDate,
        PaidDate = e.PaidDate,
        InterestAmount = e.InterestAmount,
        PrincipalAmount = e.PrincipalAmount,
        EscrowAmount = e.EscrowAmount,
        TotalAmount = e.TotalAmount,
        BalanceAfter = e.BalanceAfter,
        Status = e.Status,
        PaymentDoesNotCoverInterest = e.PaymentDoesNotCoverInterest,
    };
}

public class CreateLoanRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Lender { get; set; } = string.Empty;

    [Range(0, 999_999_999)]
    public decimal OriginalAmount { get; set; }

    /// <summary>Outstanding balance; when omitted, defaults to the original amount.</summary>
    [Range(0, 999_999_999)]
    public decimal? CurrentBalance { get; set; }

    [Range(0, 100)]
    public decimal AnnualInterestRatePct { get; set; }

    [Range(1, 1200)]
    public int TermMonths { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    [Range(1, 31)]
    public int DayOfMonthDue { get; set; } = 1;

    [Range(0, 9_999_999)]
    public decimal MonthlyPrincipalInterest { get; set; }

    [Range(0, 9_999_999)]
    public decimal MonthlyEscrow { get; set; }

    public bool EscrowCoversTaxes { get; set; }
    public bool EscrowCoversInsurance { get; set; }
    public LoanStatus Status { get; set; } = LoanStatus.Active;

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdateLoanRequest
{
    [MaxLength(200)]
    public string? Lender { get; set; }

    [Range(0, 999_999_999)]
    public decimal? OriginalAmount { get; set; }

    [Range(0, 999_999_999)]
    public decimal? CurrentBalance { get; set; }

    [Range(0, 100)]
    public decimal? AnnualInterestRatePct { get; set; }

    [Range(1, 1200)]
    public int? TermMonths { get; set; }

    public DateTime? StartDate { get; set; }

    [Range(1, 31)]
    public int? DayOfMonthDue { get; set; }

    [Range(0, 9_999_999)]
    public decimal? MonthlyPrincipalInterest { get; set; }

    [Range(0, 9_999_999)]
    public decimal? MonthlyEscrow { get; set; }

    public bool? EscrowCoversTaxes { get; set; }
    public bool? EscrowCoversInsurance { get; set; }
    public LoanStatus? Status { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
