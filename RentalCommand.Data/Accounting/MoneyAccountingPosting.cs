using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Data.Accounting;

/// <summary>
/// Builds the accounting consequence of money commands. The caller owns the transaction and
/// final save; these methods only validate facts and attach journal rows to the scoped context.
/// </summary>
public static class MoneyAccountingPosting
{
    private const int PostingRuleVersion = 1;
    private const string OperatingCash = "operating-cash";
    private const string AccountsPayable = "accounts-payable";
    private const string MortgagePayable = "mortgage-payable";
    private const string MortgageInterest = "mortgage-interest";
    private const string MortgageEscrow = "mortgage-escrow-asset";
    private const string OwnerDistributions = "owner-distributions";
    private const string Buildings = "buildings-and-improvements";
    private const string DepreciationExpense = "depreciation-expense";
    private const string AccumulatedDepreciation = "accumulated-depreciation";
    private const string RetainedEarnings = "retained-earnings";

    public static async Task<JournalEntry?> PostExpenseOccurrenceAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        Expense expense,
        int actorUserId,
        CancellationToken ct = default,
        string? actorLabel = null)
    {
        if (expense.Status is ExpenseStatus.Draft or ExpenseStatus.Rejected)
            return null;

        var currency = await PortfolioCurrencyAsync(db, expense.PortfolioId, ct);
        if (expense.Status == ExpenseStatus.Paid || expense.PaidAt is not null)
        {
            return await PostExpensePaymentAsync(
                db, context, expense, expense.Id, $"expense-payment:{expense.Id}",
                currency, actorUserId, ct, actorLabel);
        }

        return await PostBillIncurredAsync(
            db, context, expense, expense.Id, $"bill-incurred:{expense.Id}", currency, actorUserId, ct,
            actorLabel);
    }

    /// <summary>
    /// Reverses the currently effective expense journals and posts the replacement consequence
    /// for the edited expense. The caller owns the transaction and final save.
    /// </summary>
    public static async Task<bool> CorrectExpenseAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        Expense expense,
        int actorUserId,
        CancellationToken ct = default)
    {
        var current = await CurrentExpenseJournalsAsync(db, expense, ct);
        if (current.Count == 0)
            return false;

        foreach (var original in current)
        {
            await ReverseAsync(
                db,
                context,
                original,
                ReversalSourceId(original.Id),
                $"expense-correction-reversal:{expense.Id}:{original.Id}",
                actorUserId,
                ct);
        }

        if (expense.Status is ExpenseStatus.Draft or ExpenseStatus.Rejected)
            return true;

        var currency = await PortfolioCurrencyAsync(db, expense.PortfolioId, ct);
        var sourceBaseId = checked((long)current[^1].Id * 10_000L + 2L);
        var hasBillIncurred = current.Any(entry => entry.SourceType == JournalSourceType.BillIncurred);
        var hasBillPayment = current.Any(entry => entry.SourceType == JournalSourceType.BillPayment);
        if ((expense.Status == ExpenseStatus.Paid || expense.PaidAt is not null) && hasBillIncurred)
        {
            await PostBillIncurredAsync(
                db,
                context,
                expense,
                sourceBaseId,
                $"expense-correction-bill-incurred:{expense.Id}:{current[^1].Id}",
                currency,
                actorUserId,
                ct,
                actorLabel: null);
            if (hasBillPayment)
            {
                await PostBillPaymentAsync(
                    db,
                    context,
                    expense,
                    checked(sourceBaseId + 1L),
                    $"expense-correction-bill-payment:{expense.Id}:{current[^1].Id}",
                    actorUserId,
                    ct);
            }
        }
        else if (expense.Status == ExpenseStatus.Paid || expense.PaidAt is not null)
        {
            await PostExpensePaymentAsync(
                db,
                context,
                expense,
                sourceBaseId,
                $"expense-correction-payment:{expense.Id}:{current[^1].Id}",
                currency,
                actorUserId,
                ct,
                actorLabel: null);
        }
        else
        {
            await PostBillIncurredAsync(
                db,
                context,
                expense,
                sourceBaseId,
                $"expense-correction-bill-incurred:{expense.Id}:{current[^1].Id}",
                currency,
                actorUserId,
                ct,
                actorLabel: null);
        }

        return true;
    }

    /// <summary>Reverses the currently effective journals when an expense is soft-deleted.</summary>
    public static async Task<bool> ReverseExpenseJournalsAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        Expense expense,
        int actorUserId,
        CancellationToken ct = default)
    {
        var current = await CurrentExpenseJournalsAsync(db, expense, ct);
        foreach (var original in current)
        {
            await ReverseAsync(
                db,
                context,
                original,
                ReversalSourceId(original.Id),
                $"expense-delete-reversal:{expense.Id}:{original.Id}",
                actorUserId,
                ct);
        }

        return current.Count > 0;
    }

    public static async Task<JournalEntry> PostBillPaymentAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        Expense expense,
        long sourceId,
        string sourceBusinessKey,
        int actorUserId,
        CancellationToken ct = default)
    {
        var currency = await PortfolioCurrencyAsync(db, expense.PortfolioId, ct);
        var payable = await AccountAsync(db, expense.PortfolioId, AccountsPayable, ct);
        var cash = await AccountAsync(db, expense.PortfolioId, OperatingCash, ct);
        var proposal = Proposal(
            context,
            expense.PortfolioId,
            JournalSourceType.BillPayment,
            sourceId,
            sourceBusinessKey,
            DateOnly.FromDateTime(expense.IncurredAt),
            currency,
            $"Paid bill: {expense.Description}",
            actorUserId,
            [
                Debit(payable, expense.Amount, "debit:accounts-payable", expense.PropertyId, expense.UnitId,
                    memo: expense.Description, sourceLineId: expense.Id),
                Credit(cash, expense.Amount, "credit:operating-cash", expense.PropertyId, expense.UnitId,
                    memo: expense.Description, sourceLineId: expense.Id),
            ]);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    public static async Task<JournalEntry> PostExpensePaymentAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        Expense expense,
        long sourceId,
        string sourceBusinessKey,
        string currency,
        int actorUserId,
        CancellationToken ct = default,
        string? actorLabel = null)
    {
        var expenseAccount = await AccountAsync(
            db, expense.PortfolioId, ExpenseSystemKey(expense.Category), ct);
        var cash = await AccountAsync(db, expense.PortfolioId, OperatingCash, ct);
        var proposal = Proposal(
            context,
            expense.PortfolioId,
            JournalSourceType.ExpensePayment,
            sourceId,
            sourceBusinessKey,
            DateOnly.FromDateTime(expense.PaidAt ?? expense.IncurredAt),
            currency,
            $"Paid expense: {expense.Description}",
            actorUserId,
            [
                Debit(expenseAccount, expense.Amount, "debit:expense", expense.PropertyId, expense.UnitId,
                    memo: expense.Description, sourceLineId: expense.Id),
                Credit(cash, expense.Amount, "credit:operating-cash", expense.PropertyId, expense.UnitId,
                    memo: expense.Description, sourceLineId: expense.Id),
            ],
            actorLabel: actorLabel);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    public static async Task<JournalEntry> PostBankMatchedExpenseAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        Expense expense,
        BankTransaction transaction,
        int actorUserId,
        CancellationToken ct = default)
    {
        var amount = Math.Abs(transaction.Amount);
        if (amount <= 0m)
            throw new AccountingPostingValidationException("A matched expense must have a positive bank amount.");

        var directPayment = await db.JournalEntries.SingleOrDefaultAsync(entry =>
            entry.PortfolioId == expense.PortfolioId
            && entry.SourceType == JournalSourceType.ExpensePayment
            && entry.ReversesJournalEntryId == null
            && entry.Lines.Any(line => line.SourceLineId == expense.Id)
            && !db.JournalEntries.Any(reversal =>
                reversal.PortfolioId == entry.PortfolioId
                && reversal.ReversesJournalEntryId == entry.Id), ct);
        if (directPayment is not null)
            return directPayment;

        var existingBillPayment = await db.JournalEntries.SingleOrDefaultAsync(entry =>
            entry.PortfolioId == expense.PortfolioId
            && entry.SourceType == JournalSourceType.BillPayment
            && entry.ReversesJournalEntryId == null
            && entry.Lines.Any(line => line.SourceLineId == expense.Id)
            && !db.JournalEntries.Any(reversal =>
                reversal.PortfolioId == entry.PortfolioId
                && reversal.ReversesJournalEntryId == entry.Id), ct);
        if (existingBillPayment is not null)
            return existingBillPayment;

        var incurred = await db.JournalEntries.SingleOrDefaultAsync(entry =>
            entry.PortfolioId == expense.PortfolioId
            && entry.SourceType == JournalSourceType.BillIncurred
            && entry.ReversesJournalEntryId == null
            && entry.Lines.Any(line => line.SourceLineId == expense.Id)
            && !db.JournalEntries.Any(reversal =>
                reversal.PortfolioId == entry.PortfolioId
                && reversal.ReversesJournalEntryId == entry.Id), ct);
        if (incurred is not null)
        {
            return await PostBillPaymentAsync(
                db, context, expense, transaction.Id, $"bank-bill-payment:{transaction.Id}", actorUserId, ct);
        }

        return await PostExpensePaymentAsync(
            db, context, expense, transaction.Id, $"bank-expense-payment:{transaction.Id}",
            transaction.IsoCurrencyCode.ToUpperInvariant(), actorUserId, ct);
    }

    public static async Task<JournalEntry?> ReverseBankMatchedExpenseAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int bankTransactionId,
        int actorUserId,
        CancellationToken ct = default)
    {
        var original = await db.JournalEntries
            .Include(entry => entry.Lines)
            .SingleOrDefaultAsync(entry =>
                entry.PortfolioId == portfolioId
                && (entry.SourceType == JournalSourceType.BillPayment
                    || entry.SourceType == JournalSourceType.ExpensePayment)
                && entry.SourceId == bankTransactionId, ct);
        if (original is null)
            return null;
        return await ReverseBankMatchedSourceAsync(
            db, context, original, bankTransactionId, actorUserId, ct);
    }

    public static async Task<JournalEntry?> ReverseBankMatchedSourceAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        JournalSourceType sourceType,
        long sourceId,
        int actorUserId,
        CancellationToken ct = default)
    {
        var original = await db.JournalEntries
            .Include(entry => entry.Lines)
            .SingleOrDefaultAsync(entry =>
                entry.PortfolioId == portfolioId
                && entry.SourceType == sourceType
                && entry.SourceId == sourceId, ct);
        if (original is null)
            return null;
        return await ReverseBankMatchedSourceAsync(
            db, context, original, sourceId, actorUserId, ct);
    }

    private static async Task<JournalEntry> ReverseBankMatchedSourceAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        JournalEntry original,
        long sourceId,
        int actorUserId,
        CancellationToken ct)
    {
        return await ReverseAsync(
            db, context, original, ReversalSourceId(original.Id),
            $"bank-match-reversal:{original.SourceType}:{sourceId}", actorUserId, ct);
    }

    /// <summary>
    /// Uses an existing loan-payment journal as evidence when a bank line is matched. A bank
    /// match creates a journal only when the payment has not already been posted elsewhere.
    /// </summary>
    public static async Task<JournalEntry?> PostBankMatchedLoanPaymentAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        LoanPayment payment,
        BankTransaction transaction,
        int actorUserId,
        CancellationToken ct = default)
    {
        var existingPayment = await db.JournalEntries.SingleOrDefaultAsync(entry =>
            entry.PortfolioId == payment.PortfolioId
            && entry.SourceType == JournalSourceType.LoanPayment
            && entry.ReversesJournalEntryId == null
            && entry.Lines.Any(line => line.SourceLineId == payment.Id)
            && !db.JournalEntries.Any(reversal =>
                reversal.PortfolioId == entry.PortfolioId
                && reversal.ReversesJournalEntryId == entry.Id), ct);
        if (existingPayment is not null)
            return null;

        return await PostLoanPaymentAsync(
            db,
            context,
            payment,
            actorUserId,
            ct,
            sourceId: transaction.Id,
            sourceBusinessKey: $"bank-loan-payment:{transaction.Id}");
    }

    /// <summary>
    /// Reverses only the loan-payment journal created by this bank match. A clear operation must
    /// not reverse a payment journal that existed before the match.
    /// </summary>
    public static async Task<JournalEntry?> ReverseBankMatchedLoanPaymentAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int bankTransactionId,
        int loanPaymentId,
        int actorUserId,
        CancellationToken ct = default)
    {
        var original = await db.JournalEntries
            .Include(entry => entry.Lines)
            .SingleOrDefaultAsync(entry =>
                entry.PortfolioId == portfolioId
                && entry.SourceType == JournalSourceType.LoanPayment
                && entry.SourceId == bankTransactionId
                && entry.SourceBusinessKey == $"bank-loan-payment:{bankTransactionId}"
                && entry.ReversesJournalEntryId == null
                && entry.Lines.Any(line => line.SourceLineId == loanPaymentId)
                && !db.JournalEntries.Any(reversal =>
                    reversal.PortfolioId == entry.PortfolioId
                    && reversal.ReversesJournalEntryId == entry.Id), ct);
        if (original is null)
            return null;

        return await ReverseBankMatchedSourceAsync(
            db, context, original, bankTransactionId, actorUserId, ct);
    }

    public static async Task<JournalEntry> PostLoanPaymentAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        LoanPayment payment,
        int actorUserId,
        CancellationToken ct = default,
        long? sourceId = null,
        string? sourceBusinessKey = null)
    {
        var currency = await PortfolioCurrencyAsync(db, payment.PortfolioId, ct);
        var principal = await AccountAsync(db, payment.PortfolioId, MortgagePayable, ct);
        var interest = await AccountAsync(db, payment.PortfolioId, MortgageInterest, ct);
        var escrow = await AccountAsync(db, payment.PortfolioId, MortgageEscrow, ct);
        var cash = await AccountAsync(db, payment.PortfolioId, OperatingCash, ct);
        var lines = new List<AccountingProposedLine>(4);
        if (payment.PrincipalAmount > 0m)
            lines.Add(Debit(principal, payment.PrincipalAmount, "debit:mortgage-principal",
                payment.Loan?.PropertyId, null, memo: "Mortgage principal", sourceLineId: payment.Id));
        if (payment.InterestAmount > 0m)
            lines.Add(Debit(interest, payment.InterestAmount, "debit:mortgage-interest",
                payment.Loan?.PropertyId, null, memo: "Mortgage interest", sourceLineId: payment.Id));
        if (payment.EscrowAmount > 0m)
            lines.Add(Debit(escrow, payment.EscrowAmount, "debit:mortgage-escrow",
                payment.Loan?.PropertyId, null, memo: "Mortgage escrow", sourceLineId: payment.Id));
        if (payment.TotalAmount <= 0m)
            throw new AccountingPostingValidationException("A loan payment must have a positive total amount.");
        lines.Add(Credit(cash, payment.TotalAmount, "credit:operating-cash",
            payment.Loan?.PropertyId, null, memo: "Mortgage payment", sourceLineId: payment.Id));

        var proposal = Proposal(
            context,
            payment.PortfolioId,
            JournalSourceType.LoanPayment,
            sourceId ?? payment.Id,
            sourceBusinessKey ?? $"loan-payment:{payment.Id}",
            DateOnly.FromDateTime(payment.PaidDate ?? payment.DueDate),
            currency,
            "Mortgage payment posted",
            actorUserId,
            lines);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    public static async Task<JournalEntry?> PostLoanPaymentCorrectionAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        LoanPayment payment,
        LoanPaymentCorrection correction,
        int actorUserId,
        CancellationToken ct = default)
    {
        if (correction.Status != LoanPaymentStatus.Paid)
            return null;

        var original = await db.JournalEntries
            .Include(entry => entry.Lines)
            .SingleOrDefaultAsync(entry =>
                entry.PortfolioId == payment.PortfolioId
                && entry.SourceType == JournalSourceType.LoanPayment
                && entry.Lines.Any(line => line.SourceLineId == payment.Id)
                && entry.ReversesJournalEntryId == null
                && !db.JournalEntries.Any(reversal =>
                    reversal.PortfolioId == entry.PortfolioId
                    && reversal.ReversesJournalEntryId == entry.Id), ct);
        if (original is not null)
        {
            await ReverseAsync(
                db,
                context,
                original,
                ReversalSourceId(original.Id),
                $"loan-payment-correction-reversal:{correction.Id}",
                actorUserId,
                ct);
        }

        var correctedPayment = new LoanPayment
        {
            Id = payment.Id,
            PortfolioId = payment.PortfolioId,
            LoanId = payment.LoanId,
            DueDate = correction.DueDate,
            PaidDate = correction.PaidDate,
            PrincipalAmount = correction.PrincipalAmount,
            InterestAmount = correction.InterestAmount,
            EscrowAmount = correction.EscrowAmount,
            TotalAmount = correction.TotalAmount,
            BalanceAfter = correction.BalanceAfter,
            Status = correction.Status,
            Loan = payment.Loan,
        };
        return await PostLoanPaymentAsync(
            db,
            context,
            correctedPayment,
            actorUserId,
            ct,
            sourceId: correction.Id,
            sourceBusinessKey: $"loan-payment-correction:{correction.Id}");
    }

    public static async Task<JournalEntry> PostOwnerDistributionAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        OwnerDistribution distribution,
        int actorUserId,
        CancellationToken ct = default)
    {
        var currency = await PortfolioCurrencyAsync(db, distribution.PortfolioId, ct);
        var equity = await AccountAsync(db, distribution.PortfolioId, OwnerDistributions, ct);
        var cash = await AccountAsync(db, distribution.PortfolioId, OperatingCash, ct);
        var proposal = Proposal(
            context,
            distribution.PortfolioId,
            JournalSourceType.OwnerDistribution,
            distribution.Id,
            $"owner-distribution:{distribution.Id}",
            DateOnly.FromDateTime(distribution.Date),
            currency,
            "Owner distribution paid",
            actorUserId,
            [
                Debit(equity, distribution.Amount, "debit:owner-distribution", distribution.PropertyId, null,
                    memo: "Owner distribution", sourceLineId: distribution.Id,
                    ownerEntityId: distribution.OwnerEntityId),
                Credit(cash, distribution.Amount, "credit:operating-cash", distribution.PropertyId, null,
                    memo: "Owner distribution", sourceLineId: distribution.Id,
                    ownerEntityId: distribution.OwnerEntityId),
            ]);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    public static async Task<JournalEntry> PostCapitalPurchaseAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CapitalAsset asset,
        int actorUserId,
        CancellationToken ct = default)
    {
        var currency = await PortfolioCurrencyAsync(db, asset.PortfolioId, ct);
        var basis = await AccountAsync(db, asset.PortfolioId, Buildings, ct);
        var cash = await AccountAsync(db, asset.PortfolioId, OperatingCash, ct);
        var proposal = Proposal(
            context,
            asset.PortfolioId,
            JournalSourceType.CapitalPurchase,
            asset.Id,
            $"capital-purchase:{asset.Id}",
            DateOnly.FromDateTime(asset.InServiceDate),
            currency,
            $"Capital purchase: {asset.Description}",
            actorUserId,
            [
                Debit(basis, asset.CostBasis, "debit:asset", asset.PropertyId, asset.UnitId,
                    memo: asset.Description, sourceLineId: asset.Id),
                Credit(cash, asset.CostBasis, "credit:operating-cash", asset.PropertyId, asset.UnitId,
                    memo: asset.Description, sourceLineId: asset.Id),
            ]);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    public static async Task<JournalEntry?> PostDepreciationAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CapitalAsset asset,
        decimal amount,
        int year,
        int actorUserId,
        CancellationToken ct = default)
    {
        if (amount <= 0m)
            return null;
        var expense = await AccountAsync(db, asset.PortfolioId, DepreciationExpense, ct);
        var accumulated = await AccountAsync(db, asset.PortfolioId, AccumulatedDepreciation, ct);
        var currency = await PortfolioCurrencyAsync(db, asset.PortfolioId, ct);
        var sourceId = ((long)asset.Id * 10_000L) + year;
        var proposal = Proposal(
            context,
            asset.PortfolioId,
            JournalSourceType.Depreciation,
            sourceId,
            $"depreciation:{asset.Id}:{year}",
            new DateOnly(year, 12, 31),
            currency,
            $"Depreciation: {asset.Description}",
            actorUserId,
            [
                Debit(expense, amount, "debit:expense", asset.PropertyId, asset.UnitId,
                    memo: asset.Description, sourceLineId: asset.Id),
                Credit(accumulated, amount, "credit:asset", asset.PropertyId, asset.UnitId,
                    memo: asset.Description, sourceLineId: asset.Id),
            ]);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    public static async Task<JournalEntry> PostTenantOpeningBalanceAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry opening,
        int actorUserId,
        CancellationToken ct = default)
    {
        var receivable = await AccountAsync(db, opening.PortfolioId, "tenant-accounts-receivable", ct);
        var retained = await AccountAsync(db, opening.PortfolioId, RetainedEarnings, ct);
        var lines = opening.Direction == TenantLedgerDirection.Debit
            ? new[]
            {
                Debit(receivable, opening.Amount, "debit:tenant-receivable", null, null,
                    tenantAccountId: opening.TenantAccountId, memo: opening.Description,
                    sourceLineId: opening.Id),
                Credit(retained, opening.Amount, "credit:asset", null, null,
                    tenantAccountId: opening.TenantAccountId, memo: opening.Description,
                    sourceLineId: opening.Id),
            }
            : new[]
            {
                Debit(retained, opening.Amount, "debit:asset", null, null,
                    tenantAccountId: opening.TenantAccountId, memo: opening.Description,
                    sourceLineId: opening.Id),
                Credit(receivable, opening.Amount, "credit:tenant-receivable", null, null,
                    tenantAccountId: opening.TenantAccountId, memo: opening.Description,
                    sourceLineId: opening.Id),
            };
        var proposal = Proposal(
            context,
            opening.PortfolioId,
            JournalSourceType.OpeningBalance,
            opening.Id,
            opening.BusinessKey,
            opening.EffectiveOn,
            opening.Currency,
            opening.Description,
            actorUserId,
            lines);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    public static async Task<JournalEntry> PostOpeningSecurityDepositAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        SecurityDepositEntry deposit,
        int actorUserId,
        CancellationToken ct = default)
    {
        var trust = await AccountAsync(db, deposit.PortfolioId, "security-deposit-trust-cash", ct);
        var payable = await AccountAsync(db, deposit.PortfolioId, "tenant-security-deposits-payable", ct);
        var tenantAccountId = deposit.SecurityDepositAccount?.TenantAccountId;
        var proposal = Proposal(
            context,
            deposit.PortfolioId,
            JournalSourceType.SecurityDepositReceipt,
            deposit.Id,
            deposit.BusinessKey,
            deposit.EffectiveOn,
            deposit.Currency,
            deposit.Description,
            actorUserId,
            [
                Debit(trust, deposit.Amount, "debit:security-deposit-trust-cash", null, null,
                    tenantAccountId: tenantAccountId, memo: deposit.Description,
                    sourceLineId: deposit.Id),
                Credit(payable, deposit.Amount, "credit:security-deposit-payable", null, null,
                    tenantAccountId: tenantAccountId, memo: deposit.Description,
                    sourceLineId: deposit.Id),
            ]);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    public static async Task<JournalEntry> PostProviderSettlementAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        BankTransaction transaction,
        int actorUserId,
        CancellationToken ct = default)
    {
        if (transaction.Amount <= 0m)
            throw new AccountingPostingValidationException("A provider settlement must have a positive bank amount.");
        var cash = await AccountAsync(db, transaction.PortfolioId, OperatingCash, ct);
        var undeposited = await AccountAsync(db, transaction.PortfolioId, "undeposited-funds", ct);
        var currency = transaction.IsoCurrencyCode.ToUpperInvariant();
        var proposal = Proposal(
            context,
            transaction.PortfolioId,
            JournalSourceType.ProviderSettlement,
            transaction.Id,
            $"provider-settlement:{transaction.ProviderTransactionId}",
            DateOnly.FromDateTime(transaction.PostedAt),
            currency,
            $"Provider settlement: {transaction.Description}",
            actorUserId,
            [
                Debit(cash, transaction.Amount, "debit:operating-cash", transaction.PropertyId, null,
                    memo: transaction.Description, sourceLineId: transaction.Id),
                Credit(undeposited, transaction.Amount, "credit:undeposited-funds", transaction.PropertyId, null,
                    memo: transaction.Description, sourceLineId: transaction.Id),
            ]);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    public static async Task<JournalEntry?> PostBankTransferAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        BankTransaction first,
        BankTransaction second,
        int actorUserId,
        CancellationToken ct = default,
        string sourceSystemKey = OperatingCash,
        string destinationSystemKey = OperatingCash)
    {
        if (first.PortfolioId != second.PortfolioId)
            throw new AccountingPostingValidationException("Bank transfer accounts must belong to the same portfolio.");
        if (first.Amount == 0m || second.Amount == 0m || Math.Sign(first.Amount) == Math.Sign(second.Amount))
            throw new AccountingPostingValidationException("A bank transfer needs one incoming and one outgoing statement line.");
        if (Math.Abs(first.Amount) != Math.Abs(second.Amount))
            throw new AccountingPostingValidationException("A bank transfer needs matching incoming and outgoing amounts.");
        if (!string.Equals(first.IsoCurrencyCode, second.IsoCurrencyCode, StringComparison.OrdinalIgnoreCase))
            throw new AccountingPostingValidationException("A bank transfer needs matching statement currencies.");
        var destination = first.Amount > 0m ? first : second;
        var source = first.Amount > 0m ? second : first;
        var amount = Math.Abs(destination.Amount);
        var destinationAccount = await AccountAsync(db, first.PortfolioId, destinationSystemKey, ct);
        var sourceAccount = await AccountAsync(db, first.PortfolioId, sourceSystemKey, ct);
        if (destinationAccount == sourceAccount)
            return null;
        var sourceId = Math.Min(first.Id, second.Id);
        var proposal = Proposal(
            context,
            first.PortfolioId,
            JournalSourceType.BankTransfer,
            sourceId,
            $"bank-transfer:{sourceId}:{Math.Max(first.Id, second.Id)}",
            DateOnly.FromDateTime(destination.PostedAt),
            destination.IsoCurrencyCode.ToUpperInvariant(),
            "Bank transfer",
            actorUserId,
            [
                Debit(destinationAccount, amount, "debit:cash", destination.PropertyId, null,
                    memo: destination.Description, sourceLineId: destination.Id),
                Credit(sourceAccount, amount, "credit:cash", source.PropertyId, null,
                    memo: source.Description, sourceLineId: source.Id),
            ]);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    private static async Task<JournalEntry> PostBillIncurredAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        Expense expense,
        long sourceId,
        string sourceBusinessKey,
        string currency,
        int actorUserId,
        CancellationToken ct,
        string? actorLabel)
    {
        var expenseAccount = await AccountAsync(
            db, expense.PortfolioId, ExpenseSystemKey(expense.Category), ct);
        var payable = await AccountAsync(db, expense.PortfolioId, AccountsPayable, ct);
        var proposal = Proposal(
            context,
            expense.PortfolioId,
            JournalSourceType.BillIncurred,
            sourceId,
            sourceBusinessKey,
            DateOnly.FromDateTime(expense.IncurredAt),
            currency,
            $"Bill incurred: {expense.Description}",
            actorUserId,
            [
                Debit(expenseAccount, expense.Amount, "debit:expense", expense.PropertyId, expense.UnitId,
                    memo: expense.Description, sourceLineId: expense.Id),
                Credit(payable, expense.Amount, "credit:accounts-payable", expense.PropertyId, expense.UnitId,
                    memo: expense.Description, sourceLineId: expense.Id),
            ],
            actorLabel: actorLabel);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    private static Task<List<JournalEntry>> CurrentExpenseJournalsAsync(
        RentalCommandDbContext db,
        Expense expense,
        CancellationToken ct) =>
        db.JournalEntries
            .Include(entry => entry.Lines)
            .Where(entry => entry.PortfolioId == expense.PortfolioId
                && entry.ReversesJournalEntryId == null
                && (entry.SourceType == JournalSourceType.ExpensePayment
                    || entry.SourceType == JournalSourceType.BillIncurred
                    || entry.SourceType == JournalSourceType.BillPayment)
                && entry.Lines.Any(line => line.SourceLineId == expense.Id)
                && !db.JournalEntries.Any(reversal =>
                    reversal.PortfolioId == entry.PortfolioId
                    && reversal.ReversesJournalEntryId == entry.Id))
            .OrderBy(entry => entry.Id)
            .ToListAsync(ct);

    private static async Task<int> AccountAsync(
        RentalCommandDbContext db, int portfolioId, string systemKey, CancellationToken ct) =>
        await AccountingPostingSupport.RequireSystemAccountIdAsync(db, portfolioId, systemKey, ct);

    private static async Task<string> PortfolioCurrencyAsync(
        RentalCommandDbContext db, int portfolioId, CancellationToken ct) =>
        await db.Portfolios.Where(portfolio => portfolio.Id == portfolioId)
            .Select(portfolio => portfolio.Currency)
            .SingleOrDefaultAsync(ct)
        ?? throw new AccountingPostingValidationException("The portfolio currency is unavailable.");

    private static AccountingProposedEntry Proposal(
        IAtomicCommandContext context,
        int portfolioId,
        JournalSourceType sourceType,
        long sourceId,
        string sourceBusinessKey,
        DateOnly effectiveOn,
        string currency,
        string description,
        int actorUserId,
        IEnumerable<AccountingProposedLine> lines,
        string? actorLabel = null) =>
        AccountingPostingSupport.BuildProposal(
            context,
            portfolioId,
            sourceType,
            sourceId,
            sourceBusinessKey,
            PostingRuleVersion,
            effectiveOn,
            currency,
            description,
            lines,
            userId: actorUserId > 0 ? actorUserId : null,
            actorLabel: actorLabel);

    private static AccountingProposedLine Debit(
        int accountId,
        decimal amount,
        string sourceLineType,
        int? propertyId,
        int? unitId,
        int? tenantAccountId = null,
        string? memo = null,
        long? sourceLineId = null,
        int? ownerEntityId = null) => new()
    {
        LedgerAccountId = accountId,
        DebitAmount = amount,
        PropertyId = propertyId,
        UnitId = unitId,
        TenantAccountId = tenantAccountId,
        OwnerEntityId = ownerEntityId,
        SourceLineType = sourceLineType,
        SourceLineId = sourceLineId,
        Memo = memo,
    };

    private static AccountingProposedLine Credit(
        int accountId,
        decimal amount,
        string sourceLineType,
        int? propertyId,
        int? unitId,
        int? tenantAccountId = null,
        string? memo = null,
        long? sourceLineId = null,
        int? ownerEntityId = null) => new()
    {
        LedgerAccountId = accountId,
        CreditAmount = amount,
        PropertyId = propertyId,
        UnitId = unitId,
        TenantAccountId = tenantAccountId,
        OwnerEntityId = ownerEntityId,
        SourceLineType = sourceLineType,
        SourceLineId = sourceLineId,
        Memo = memo,
    };

    private static async Task<JournalEntry> ReverseAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        JournalEntry original,
        long sourceId,
        string sourceBusinessKey,
        int actorUserId,
        CancellationToken ct)
    {
        var proposal = Proposal(
            context,
            original.PortfolioId,
            original.SourceType,
            sourceId,
            sourceBusinessKey,
            original.EffectiveOn,
            original.Currency,
            $"Reversal of journal entry {original.Id}",
            actorUserId,
            original.Lines.Select(line => new AccountingProposedLine
            {
                LedgerAccountId = line.LedgerAccountId,
                DebitAmount = line.CreditAmount,
                CreditAmount = line.DebitAmount,
                Memo = line.Memo,
                PropertyId = line.PropertyId,
                UnitId = line.UnitId,
                TenantAccountId = line.TenantAccountId,
                OwnerEntityId = line.OwnerEntityId,
                SourceLineType = Opposite(line.SourceLineType),
                SourceLineId = line.SourceLineId,
            })
            );
        proposal.ReversesJournalEntryId = original.Id;
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    private static string ExpenseSystemKey(ScheduleECategory category) =>
        category switch
        {
            ScheduleECategory.Insurance => "insurance",
            ScheduleECategory.ManagementFees => "management-fees",
            ScheduleECategory.MortgageInterest => "mortgage-interest",
            ScheduleECategory.Repairs or ScheduleECategory.CleaningMaintenance => "repairs-and-maintenance",
            ScheduleECategory.Taxes => "property-taxes",
            ScheduleECategory.Utilities => "utilities",
            ScheduleECategory.Depreciation => "depreciation-expense",
            _ => "other-operating-expense",
        };

    private static long ReversalSourceId(int originalJournalId) =>
        checked((long)originalJournalId * 10_000L + 1L);

    private static string? Opposite(string? sourceLineType)
    {
        if (string.IsNullOrWhiteSpace(sourceLineType))
            return sourceLineType;
        return sourceLineType.StartsWith("debit:", StringComparison.Ordinal)
            ? "credit:" + sourceLineType[6..]
            : sourceLineType.StartsWith("credit:", StringComparison.Ordinal)
                ? "debit:" + sourceLineType[7..]
                : sourceLineType;
    }
}
