namespace RentalCommand.Core.Automation;

public sealed record AtomicLoanPaymentTail(int LoanId, string PeriodKey, decimal BalanceAfter);
