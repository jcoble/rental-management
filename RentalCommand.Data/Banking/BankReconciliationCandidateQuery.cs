using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Banking;

/// <summary>
/// Canonical SQL-translatable eligibility rules for bank-reconciliation targets. Read-time
/// suggestion ranking and commit-time target revalidation must both start from these queries.
/// </summary>
public static class BankReconciliationCandidateQuery
{
    private const decimal AmountTolerance = 0.01m;

    public static IQueryable<BankReconciliationCandidate<TenantLedgerEntry>> EligibleTenantLedgerEntries(
        IQueryable<BankTransaction> transactions,
        IQueryable<TenantLedgerEntry> entries,
        bool requirePropertyMatch = true) =>
        from transaction in transactions
        from entry in entries
        where entry.PortfolioId == transaction.PortfolioId
            && ((entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && entry.Direction == TenantLedgerDirection.Credit
                    && transaction.Amount > 0m)
                || ((entry.EntryType == TenantLedgerEntryType.TransferIn
                        || entry.EntryType == TenantLedgerEntryType.TransferOut)
                    && ((entry.Direction == TenantLedgerDirection.Credit && transaction.Amount > 0m)
                        || (entry.Direction == TenantLedgerDirection.Debit && transaction.Amount < 0m))))
            && entry.Amount >= (transaction.Amount < 0m ? -transaction.Amount : transaction.Amount) - AmountTolerance
            && entry.Amount <= (transaction.Amount < 0m ? -transaction.Amount : transaction.Amount) + AmountTolerance
            && entry.EffectiveOn >= DateOnly.FromDateTime(transaction.PostedAt.AddDays(-14))
            && entry.EffectiveOn <= DateOnly.FromDateTime(transaction.PostedAt.AddDays(14))
            && (!requirePropertyMatch || (transaction.PropertyId != null
                && entry.TenantAccount!.LeaseManagement!.PropertyId == transaction.PropertyId))
        select new BankReconciliationCandidate<TenantLedgerEntry>
        {
            Transaction = transaction,
            Target = entry,
        };

    public static IQueryable<BankReconciliationCandidate<Expense>> EligibleExpenses(
        IQueryable<BankTransaction> transactions,
        IQueryable<Expense> expenses,
        bool requirePropertyMatch = true,
        bool requireMatchableState = true) =>
        from transaction in transactions
        from expense in expenses
        where expense.PortfolioId == transaction.PortfolioId
            && (!requireMatchableState || (expense.DeletedAt == null
                && (expense.Status == ExpenseStatus.Pending
                || expense.Status == ExpenseStatus.Approved
                || expense.Status == ExpenseStatus.Paid)))
            && transaction.Amount < 0m
            && expense.Amount >= -transaction.Amount - AmountTolerance
            && expense.Amount <= -transaction.Amount + AmountTolerance
            && (expense.PaidAt ?? expense.IncurredAt) >= transaction.PostedAt.AddDays(-14)
            && (expense.PaidAt ?? expense.IncurredAt) <= transaction.PostedAt.AddDays(14)
            && (!requirePropertyMatch || (expense.PropertyId == transaction.PropertyId
                || (expense.PropertyId == null && expense.Unit!.PropertyId == transaction.PropertyId)
                || (expense.PropertyId == null && expense.UnitId == null
                    && expense.WorkOrder!.PropertyId == transaction.PropertyId)
                || (transaction.PropertyId == null && expense.PropertyId == null
                    && expense.UnitId == null && expense.WorkOrderId == null)))
        select new BankReconciliationCandidate<Expense>
        {
            Transaction = transaction,
            Target = expense,
        };

    public static IQueryable<BankReconciliationCandidate<LoanPaymentEffectiveRow>> EligibleLoanPayments(
        IQueryable<BankTransaction> transactions,
        IQueryable<LoanPaymentEffectiveRow> payments,
        bool requirePropertyMatch = true) =>
        from transaction in transactions
        from payment in payments
        where payment.PortfolioId == transaction.PortfolioId
            && payment.Status == LoanPaymentStatus.Paid
            && transaction.Amount < 0m
            && payment.TotalAmount >= -transaction.Amount - AmountTolerance
            && payment.TotalAmount <= -transaction.Amount + AmountTolerance
            && (payment.PaidDate ?? payment.DueDate) >= transaction.PostedAt.AddDays(-14)
            && (payment.PaidDate ?? payment.DueDate) <= transaction.PostedAt.AddDays(14)
            && (!requirePropertyMatch || (transaction.PropertyId != null
                && payment.PropertyId == transaction.PropertyId))
        select new BankReconciliationCandidate<LoanPaymentEffectiveRow>
        {
            Transaction = transaction,
            Target = payment,
        };

    public static IQueryable<BankReconciliationCandidate<OwnerDistribution>> EligibleOwnerDistributions(
        IQueryable<BankTransaction> transactions,
        IQueryable<OwnerDistribution> distributions,
        bool requirePropertyMatch = true) =>
        from transaction in transactions
        from distribution in distributions
        where distribution.PortfolioId == transaction.PortfolioId
            && distribution.Status == OwnerDistributionStatus.Approved
            && transaction.Amount < 0m
            && distribution.Amount >= -transaction.Amount - AmountTolerance
            && distribution.Amount <= -transaction.Amount + AmountTolerance
            && distribution.Date >= transaction.PostedAt.AddDays(-14)
            && distribution.Date <= transaction.PostedAt.AddDays(14)
            && (!requirePropertyMatch || (distribution.PropertyId == transaction.PropertyId
                || (distribution.PropertyId == null && transaction.PropertyId == null)))
        select new BankReconciliationCandidate<OwnerDistribution>
        {
            Transaction = transaction,
            Target = distribution,
        };

    public static IQueryable<BankReconciliationCandidate<BankTransaction>> EligibleTransfers(
        IQueryable<BankTransaction> transactions,
        IQueryable<BankTransaction> transferTargets) =>
        from transaction in transactions
        from other in transferTargets
        where other.PortfolioId == transaction.PortfolioId
            && other.Id != transaction.Id
            && other.BankConnectionId != transaction.BankConnectionId
            && other.MatchStatus == "Unmatched"
            && other.Amount >= -transaction.Amount - AmountTolerance
            && other.Amount <= -transaction.Amount + AmountTolerance
            && other.PostedAt >= transaction.PostedAt.AddDays(-3)
            && other.PostedAt <= transaction.PostedAt.AddDays(3)
        select new BankReconciliationCandidate<BankTransaction>
        {
            Transaction = transaction,
            Target = other,
        };
}

public sealed class BankReconciliationCandidate<TTarget>
{
    public required BankTransaction Transaction { get; init; }
    public required TTarget Target { get; init; }
}
