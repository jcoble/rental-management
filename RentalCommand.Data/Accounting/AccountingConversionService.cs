using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Data.Accounting;

/// <summary>
/// Converts immutable operational history into the same journal proposals used by live posting.
/// Each source generator pages source identities in SQL and the conversion service saves only one
/// bounded batch at a time.
/// </summary>
public sealed class AccountingConversionService
{
    public const int PostingRuleVersion = 1;
    private const int DefaultBatchSize = 100;

    private readonly RentalCommandDbContext _db;
    private readonly AccountingConversionFramework _framework;
    private readonly ChartOfAccountsSeedService _chart;
    private readonly AccountingPostingService _posting;

    public AccountingConversionService(RentalCommandDbContext db)
        : this(
            db,
            new AccountingConversionFramework(
            [
                new TenantChargeSourceJournalGenerator(db),
                new TenantReceiptSourceJournalGenerator(db),
                new TenantConcessionSourceJournalGenerator(db),
                new OpeningBalanceSourceJournalGenerator(db),
                new SecurityDepositReceiptSourceJournalGenerator(db),
                new SecurityDepositRefundSourceJournalGenerator(db),
                new SecurityDepositApplicationSourceJournalGenerator(db),
                new ExpensePaymentSourceJournalGenerator(db),
                new BillIncurredSourceJournalGenerator(db),
                new BillPaymentSourceJournalGenerator(db),
                new ProviderSettlementSourceJournalGenerator(db),
                new BankTransferSourceJournalGenerator(db),
                new LoanPaymentSourceJournalGenerator(db),
                new CapitalPurchaseSourceJournalGenerator(db),
                new OwnerDistributionSourceJournalGenerator(db),
            ]),
            new ChartOfAccountsSeedService(db),
            new AccountingPostingService(db))
    {
    }

    public AccountingConversionService(
        RentalCommandDbContext db,
        AccountingConversionFramework framework,
        ChartOfAccountsSeedService chart,
        AccountingPostingService posting)
    {
        _db = db;
        _framework = framework;
        _chart = chart;
        _posting = posting;
    }

    /// <summary>
    /// Converts one portfolio without rewriting source rows. A database transaction protects each
    /// bounded page; retries are safe because the posting key is unique and the digest is stable.
    /// </summary>
    public async Task ConvertPortfolioAsync(
        int portfolioId,
        int batchSize = DefaultBatchSize,
        CancellationToken ct = default)
    {
        if (portfolioId <= 0)
            throw new ArgumentOutOfRangeException(nameof(portfolioId));
        if (batchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _chart.SeedAsync(portfolioId, ct);
        await _db.SaveChangesAsync(ct);

        foreach (var sourceType in _framework.SourceTypes)
        {
            long afterSourceId = 0;
            while (true)
            {
                var sourceIds = await _framework.GetSourceIdsAsync(
                    portfolioId, sourceType, afterSourceId, batchSize, ct);
                if (sourceIds.Count == 0)
                    break;

                foreach (var sourceId in sourceIds)
                {
                    var key = new AccountingSourceJournalKey(
                        portfolioId, sourceType, sourceId, PostingRuleVersion);
                    var proposal = await _framework.GenerateAsync(key, ct);
                    if (proposal is not null)
                        await _posting.PostAsync(proposal, ct);
                    afterSourceId = sourceId;
                }

                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
            }

            await UpsertReconciliationAsync(portfolioId, sourceType, ct);
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task UpsertReconciliationAsync(
        int portfolioId,
        JournalSourceType sourceType,
        CancellationToken ct)
    {
        var currency = await _db.Portfolios
            .Where(portfolio => portfolio.Id == portfolioId)
            .Select(portfolio => portfolio.Currency)
            .SingleAsync(ct);
        var sourceTotal = await _framework.GetSourceTotalAsync(
            portfolioId, sourceType, currency, ct);
        var postedDebit = await _db.JournalLines
            .Where(line => line.JournalEntry!.PortfolioId == portfolioId
                && line.JournalEntry.SourceType == sourceType
                && line.JournalEntry.PostingRuleVersion == PostingRuleVersion
                && line.JournalEntry.Currency == currency)
            .Select(line => (decimal?)line.DebitAmount)
            .SumAsync(ct) ?? 0m;
        var postedCredit = await _db.JournalLines
            .Where(line => line.JournalEntry!.PortfolioId == portfolioId
                && line.JournalEntry.SourceType == sourceType
                && line.JournalEntry.PostingRuleVersion == PostingRuleVersion
                && line.JournalEntry.Currency == currency)
            .Select(line => (decimal?)line.CreditAmount)
            .SumAsync(ct) ?? 0m;

        var row = await _db.AccountingConversionReconciliations
            .SingleOrDefaultAsync(existing => existing.PortfolioId == portfolioId
                && existing.SourceType == sourceType
                && existing.Currency == currency
                && existing.PostingRuleVersion == PostingRuleVersion, ct);
        if (row is null)
        {
            row = new AccountingConversionReconciliation
            {
                PortfolioId = portfolioId,
                SourceType = sourceType,
                Currency = currency,
                PostingRuleVersion = PostingRuleVersion,
                CreatedAtUtc = DateTime.UtcNow,
            };
            _db.AccountingConversionReconciliations.Add(row);
        }

        row.SourceTotal = sourceTotal;
        row.PostedDebitTotal = postedDebit;
        row.PostedCreditTotal = postedCredit;
        row.ImbalanceAmount = postedDebit - postedCredit;
        row.UpdatedAtUtc = DateTime.UtcNow;
    }
}

/// <summary>Common SQL paging and deterministic proposal identity for historical generators.</summary>
public abstract class AccountingSourceJournalGeneratorBase : IAccountingSourceJournalGenerator
{
    protected AccountingSourceJournalGeneratorBase(RentalCommandDbContext db, JournalSourceType sourceType)
    {
        Db = db;
        SourceType = sourceType;
    }

    protected RentalCommandDbContext Db { get; }
    public JournalSourceType SourceType { get; }

    public abstract Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default);

    public abstract Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default);

    public abstract Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default);

    protected static IAtomicCommandContext ContextFor(AccountingSourceJournalKey key) =>
        new ConversionAtomicCommandContext(key);

    protected static AccountingProposedEntry Proposal(
        AccountingSourceJournalKey key,
        DateOnly effectiveOn,
        string currency,
        string description,
        string sourceBusinessKey,
        IEnumerable<AccountingProposedLine> lines,
        int? reversesJournalEntryId = null) =>
        AccountingPostingSupport.BuildProposal(
            ContextFor(key),
            key.PortfolioId,
            key.SourceType,
            key.SourceId,
            sourceBusinessKey,
            key.PostingRuleVersion,
            effectiveOn,
            currency,
            description,
            lines,
            reversesJournalEntryId,
            actorLabel: "system:accounting-conversion");

    protected Task<int> AccountAsync(int portfolioId, string systemKey, CancellationToken ct) =>
        AccountingPostingSupport.RequireSystemAccountIdAsync(Db, portfolioId, systemKey, ct);

    protected Task<string> PortfolioCurrencyAsync(int portfolioId, CancellationToken ct) =>
        Db.Portfolios.Where(portfolio => portfolio.Id == portfolioId)
            .Select(portfolio => portfolio.Currency)
            .SingleAsync(ct);

    protected static AccountingProposedLine Debit(
        int accountId,
        decimal amount,
        string lineType,
        string? memo,
        long sourceLineId,
        int? propertyId = null,
        int? unitId = null,
        int? tenantAccountId = null,
        int? ownerEntityId = null) => new()
        {
            LedgerAccountId = accountId,
            DebitAmount = amount,
            Memo = memo,
            SourceLineType = lineType,
            SourceLineId = sourceLineId,
            PropertyId = propertyId,
            UnitId = unitId,
            TenantAccountId = tenantAccountId,
            OwnerEntityId = ownerEntityId,
        };

    protected static AccountingProposedLine Credit(
        int accountId,
        decimal amount,
        string lineType,
        string? memo,
        long sourceLineId,
        int? propertyId = null,
        int? unitId = null,
        int? tenantAccountId = null,
        int? ownerEntityId = null) => new()
        {
            LedgerAccountId = accountId,
            CreditAmount = amount,
            Memo = memo,
            SourceLineType = lineType,
            SourceLineId = sourceLineId,
            PropertyId = propertyId,
            UnitId = unitId,
            TenantAccountId = tenantAccountId,
            OwnerEntityId = ownerEntityId,
        };
}

/// <summary>Converts paid expenses using the live ExpensePayment posting rule.</summary>
public sealed class ExpensePaymentSourceJournalGenerator : AccountingSourceJournalGeneratorBase
{
    public ExpensePaymentSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.ExpensePayment) { }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.Expenses
            .Where(expense => expense.PortfolioId == portfolioId
                && expense.Id > afterSourceId
                && expense.Status != ExpenseStatus.Draft
                && expense.Status != ExpenseStatus.Rejected
                && (expense.Status == ExpenseStatus.Paid || expense.PaidAt != null)
                && expense.CapitalizedAssetId == null)
            .OrderBy(expense => expense.Id)
            .Select(expense => (long)expense.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var expense = await Db.Expenses
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.PortfolioId == key.PortfolioId
                && item.Id == key.SourceId, ct);
        if (expense is null
            || expense.Status is ExpenseStatus.Draft or ExpenseStatus.Rejected
            || expense.CapitalizedAssetId is not null
            || (expense.Status != ExpenseStatus.Paid && expense.PaidAt is null))
            return null;

        var currency = await PortfolioCurrencyAsync(key.PortfolioId, ct);
        var expenseAccount = await AccountAsync(
            key.PortfolioId, ExpenseSystemKeyForConversion(expense.Category), ct);
        var cash = await AccountAsync(key.PortfolioId, "operating-cash", ct);
        var effectiveOn = DateOnly.FromDateTime(expense.PaidAt ?? expense.IncurredAt);
        return Proposal(
            key,
            effectiveOn,
            currency,
            $"Paid expense: {expense.Description}",
            $"expense-payment:{expense.Id}",
            [
                Debit(expenseAccount, expense.Amount, "debit:expense", expense.Description,
                    expense.Id, expense.PropertyId, expense.UnitId),
                Credit(cash, expense.Amount, "credit:operating-cash", expense.Description,
                    expense.Id, expense.PropertyId, expense.UnitId),
            ]);
    }

    public override async Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) =>
        await Db.Expenses
            .Where(expense => expense.PortfolioId == portfolioId
                && expense.Status != ExpenseStatus.Draft
                && expense.Status != ExpenseStatus.Rejected
                && (expense.Status == ExpenseStatus.Paid || expense.PaidAt != null)
                && expense.CapitalizedAssetId == null)
            .Select(expense => (decimal?)expense.Amount)
            .SumAsync(ct) ?? 0m;

    internal static string ExpenseSystemKeyForConversion(ScheduleECategory category) =>
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
}

/// <summary>Converts unpaid expenses using the live BillIncurred posting rule.</summary>
public sealed class BillIncurredSourceJournalGenerator : AccountingSourceJournalGeneratorBase
{
    public BillIncurredSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.BillIncurred) { }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.Expenses
            .Where(expense => expense.PortfolioId == portfolioId
                && expense.Id > afterSourceId
                && expense.Status != ExpenseStatus.Draft
                && expense.Status != ExpenseStatus.Rejected
                && expense.Status != ExpenseStatus.Paid
                && expense.PaidAt == null
                && expense.CapitalizedAssetId == null)
            .OrderBy(expense => expense.Id)
            .Select(expense => (long)expense.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var expense = await Db.Expenses.AsNoTracking().SingleOrDefaultAsync(item =>
            item.PortfolioId == key.PortfolioId && item.Id == key.SourceId, ct);
        if (expense is null
            || expense.Status is ExpenseStatus.Draft or ExpenseStatus.Rejected or ExpenseStatus.Paid
            || expense.PaidAt is not null
            || expense.CapitalizedAssetId is not null)
            return null;

        var currency = await PortfolioCurrencyAsync(key.PortfolioId, ct);
        var expenseAccount = await AccountAsync(
            key.PortfolioId, ExpensePaymentSourceJournalGenerator.ExpenseSystemKeyForConversion(expense.Category), ct);
        var payable = await AccountAsync(key.PortfolioId, "accounts-payable", ct);
        return Proposal(
            key,
            DateOnly.FromDateTime(expense.IncurredAt),
            currency,
            $"Bill incurred: {expense.Description}",
            $"bill-incurred:{expense.Id}",
            [
                Debit(expenseAccount, expense.Amount, "debit:expense", expense.Description,
                    expense.Id, expense.PropertyId, expense.UnitId),
                Credit(payable, expense.Amount, "credit:accounts-payable", expense.Description,
                    expense.Id, expense.PropertyId, expense.UnitId),
            ]);
    }

    public override async Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) =>
        await Db.Expenses
            .Where(expense => expense.PortfolioId == portfolioId
                && expense.Status != ExpenseStatus.Draft
                && expense.Status != ExpenseStatus.Rejected
                && expense.Status != ExpenseStatus.Paid
                && expense.PaidAt == null
                && expense.CapitalizedAssetId == null)
            .Select(expense => (decimal?)expense.Amount)
            .SumAsync(ct) ?? 0m;
}

/// <summary>Shared conversion rules for tenant charges, receipts, concessions, and openings.</summary>
public abstract class TenantLedgerSourceJournalGeneratorBase : AccountingSourceJournalGeneratorBase
{
    private static readonly TenantLedgerEntryType[] ChargeTypes =
    [
        TenantLedgerEntryType.RentCharge,
        TenantLedgerEntryType.AddendumCharge,
        TenantLedgerEntryType.LateFeeCharge,
        TenantLedgerEntryType.DepositCharge,
        TenantLedgerEntryType.ManualCharge,
    ];

    protected TenantLedgerSourceJournalGeneratorBase(
        RentalCommandDbContext db,
        JournalSourceType sourceType,
        params TenantLedgerEntryType[] normalTypes)
        : base(db, sourceType) => NormalTypes = normalTypes;

    private IReadOnlyList<TenantLedgerEntryType> NormalTypes { get; }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.TenantLedgerEntries
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.Id > afterSourceId
                && (NormalTypes.Contains(entry.EntryType)
                    || entry.EntryType == TenantLedgerEntryType.Reversal))
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var entry = await Db.TenantLedgerEntries
            .AsNoTracking()
            .Include(item => item.TenantAccount)
                .ThenInclude(account => account!.LeaseManagement)
            .SingleOrDefaultAsync(item => item.PortfolioId == key.PortfolioId
                && item.Id == key.SourceId, ct);
        if (entry is null || entry.Amount <= 0m)
            return null;

        if (entry.ReversesEntryId is { } originalId)
        {
            var original = await Db.TenantLedgerEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.PortfolioId == key.PortfolioId
                    && item.Id == originalId, ct);
            if (original is null || !MatchesNormalType(original))
                return null;
            var originalSourceId = original.Id;
            var originalJournal = await Db.JournalEntries
                .Include(journal => journal.Lines)
                .SingleOrDefaultAsync(journal => journal.PortfolioId == key.PortfolioId
                    && journal.SourceType == SourceType
                    && journal.SourceId == originalSourceId, ct);
            if (originalJournal is null)
                return null;
            return Proposal(
                key,
                entry.EffectiveOn,
                entry.Currency.ToUpperInvariant(),
                $"Reversal: {entry.Description}",
                string.IsNullOrWhiteSpace(entry.BusinessKey)
                    ? $"tenant-reversal:{entry.Id}"
                    : entry.BusinessKey,
                originalJournal.Lines.Select(line => new AccountingProposedLine
                {
                    LedgerAccountId = line.LedgerAccountId,
                    DebitAmount = line.CreditAmount,
                    CreditAmount = line.DebitAmount,
                    Memo = line.Memo,
                    PropertyId = line.PropertyId,
                    UnitId = line.UnitId,
                    TenantAccountId = line.TenantAccountId,
                    OwnerEntityId = line.OwnerEntityId,
                    SourceLineType = OppositeLineType(line.SourceLineType),
                    SourceLineId = line.SourceLineId,
                }),
                originalJournal.Id);
        }

        if (!MatchesNormalType(entry))
            return null;
        var tenant = entry.TenantAccount;
        var propertyId = tenant?.LeaseManagement?.PropertyId;
        var unitId = tenant?.LeaseManagement?.UnitId;
        var currency = entry.Currency.ToUpperInvariant();
        var lines = new List<AccountingProposedLine>(2);
        switch (SourceType)
        {
            case JournalSourceType.TenantCharge:
            {
                var receivable = await AccountAsync(key.PortfolioId, "tenant-accounts-receivable", ct);
                var target = entry.EntryType == TenantLedgerEntryType.DepositCharge
                    ? await AccountAsync(key.PortfolioId, "tenant-security-deposits-payable", ct)
                    : await AccountAsync(key.PortfolioId, IncomeSystemKey(entry.EntryType), ct);
                lines.Add(Debit(receivable, entry.Amount, "debit:tenant-receivable", entry.Description,
                    entry.Id, propertyId, unitId, entry.TenantAccountId));
                lines.Add(Credit(
                    target,
                    entry.Amount,
                    entry.EntryType == TenantLedgerEntryType.DepositCharge
                        ? "credit:security-deposit-payable"
                        : "credit:income",
                    entry.Description,
                    entry.Id,
                    propertyId,
                    unitId,
                    entry.TenantAccountId));
                break;
            }
            case JournalSourceType.TenantReceipt:
            {
                var cash = await AccountAsync(key.PortfolioId, await CashSystemKeyAsync(entry, ct), ct);
                var receivable = await AccountAsync(key.PortfolioId, "tenant-accounts-receivable", ct);
                lines.Add(Debit(cash, entry.Amount, CashLineType(await CashSystemKeyAsync(entry, ct)),
                    entry.Description, entry.Id, propertyId, unitId, entry.TenantAccountId));
                lines.Add(Credit(receivable, entry.Amount, "credit:tenant-receivable", entry.Description,
                    entry.Id, propertyId, unitId, entry.TenantAccountId));
                break;
            }
            case JournalSourceType.TenantConcession:
            {
                var receivable = await AccountAsync(key.PortfolioId, "tenant-accounts-receivable", ct);
                var income = await AccountAsync(key.PortfolioId, IncomeSystemKey(entry.EntryType), ct);
                if (entry.Direction == TenantLedgerDirection.Credit)
                {
                    lines.Add(Debit(income, entry.Amount, "debit:income", entry.Description,
                        entry.Id, propertyId, unitId, entry.TenantAccountId));
                    lines.Add(Credit(receivable, entry.Amount, "credit:tenant-receivable", entry.Description,
                        entry.Id, propertyId, unitId, entry.TenantAccountId));
                }
                else
                {
                    lines.Add(Debit(receivable, entry.Amount, "debit:tenant-receivable", entry.Description,
                        entry.Id, propertyId, unitId, entry.TenantAccountId));
                    lines.Add(Credit(income, entry.Amount, "credit:income", entry.Description,
                        entry.Id, propertyId, unitId, entry.TenantAccountId));
                }
                break;
            }
            case JournalSourceType.OpeningBalance:
            {
                var receivable = await AccountAsync(key.PortfolioId, "tenant-accounts-receivable", ct);
                var retained = await AccountAsync(key.PortfolioId, "retained-earnings", ct);
                if (entry.Direction == TenantLedgerDirection.Debit)
                {
                    lines.Add(Debit(receivable, entry.Amount, "debit:tenant-receivable", entry.Description,
                        entry.Id, propertyId, unitId, entry.TenantAccountId));
                    lines.Add(Credit(retained, entry.Amount, "credit:asset", entry.Description,
                        entry.Id, propertyId, unitId, entry.TenantAccountId));
                }
                else
                {
                    lines.Add(Debit(retained, entry.Amount, "debit:asset", entry.Description,
                        entry.Id, propertyId, unitId, entry.TenantAccountId));
                    lines.Add(Credit(receivable, entry.Amount, "credit:tenant-receivable", entry.Description,
                        entry.Id, propertyId, unitId, entry.TenantAccountId));
                }
                break;
            }
        }

        return Proposal(
            key,
            entry.EffectiveOn,
            currency,
            string.IsNullOrWhiteSpace(entry.Description) ? "Tenant ledger entry" : entry.Description,
            string.IsNullOrWhiteSpace(entry.BusinessKey)
                ? $"tenant-ledger:{entry.Id}"
                : entry.BusinessKey,
            lines);
    }

    public override async Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) =>
        await Db.TenantLedgerEntries
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.Currency == currency
                && NormalTypes.Contains(entry.EntryType)
                && entry.ReversesEntryId == null)
            .Select(entry => (decimal?)entry.Amount)
            .SumAsync(ct) ?? 0m;

    private bool MatchesNormalType(TenantLedgerEntry entry) =>
        NormalTypes.Contains(entry.EntryType);

    private async Task<string> CashSystemKeyAsync(TenantLedgerEntry entry, CancellationToken ct)
    {
        if (entry.ProviderPaymentAttemptId is { } attemptId)
        {
            var attempt = await Db.TenantPaymentAttempts
                .AsNoTracking()
                .SingleOrDefaultAsync(attempt => attempt.PortfolioId == entry.PortfolioId
                    && attempt.Id == attemptId, ct);
            if (attempt is not null
                && !string.Equals(attempt.Provider, "manual", StringComparison.OrdinalIgnoreCase))
                return "undeposited-funds";

            var targetId = attempt?.ChargeLedgerEntryId;
            if (targetId is { } chargeId
                && await Db.TenantLedgerEntries.AnyAsync(target => target.PortfolioId == entry.PortfolioId
                    && target.Id == chargeId
                    && target.EntryType == TenantLedgerEntryType.DepositCharge, ct))
                return "security-deposit-trust-cash";
        }
        return "operating-cash";
    }

    private static string IncomeSystemKey(TenantLedgerEntryType type) =>
        type switch
        {
            TenantLedgerEntryType.LateFeeCharge => "late-fee-income",
            TenantLedgerEntryType.AddendumCharge => "other-rental-income",
            _ => "rental-income",
        };

    private static string CashLineType(string systemKey) =>
        systemKey switch
        {
            "undeposited-funds" => "debit:undeposited-funds",
            "security-deposit-trust-cash" => "debit:security-deposit-trust-cash",
            _ => "debit:operating-cash",
        };

    private static string OppositeLineType(string? lineType) =>
        lineType?.StartsWith("debit:", StringComparison.Ordinal) == true
            ? "credit:" + lineType[6..]
            : lineType?.StartsWith("credit:", StringComparison.Ordinal) == true
                ? "debit:" + lineType[7..]
                : lineType ?? string.Empty;
}

public sealed class TenantChargeSourceJournalGenerator : TenantLedgerSourceJournalGeneratorBase
{
    public TenantChargeSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.TenantCharge,
            TenantLedgerEntryType.RentCharge,
            TenantLedgerEntryType.AddendumCharge,
            TenantLedgerEntryType.LateFeeCharge,
            TenantLedgerEntryType.DepositCharge,
            TenantLedgerEntryType.ManualCharge) { }
}

public sealed class TenantReceiptSourceJournalGenerator : TenantLedgerSourceJournalGeneratorBase
{
    public TenantReceiptSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.TenantReceipt, TenantLedgerEntryType.PaymentReceipt) { }
}

public sealed class TenantConcessionSourceJournalGenerator : TenantLedgerSourceJournalGeneratorBase
{
    public TenantConcessionSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.TenantConcession,
            TenantLedgerEntryType.Credit, TenantLedgerEntryType.Adjustment) { }
}

public sealed class OpeningBalanceSourceJournalGenerator : TenantLedgerSourceJournalGeneratorBase
{
    public OpeningBalanceSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.OpeningBalance, TenantLedgerEntryType.OpeningBalance) { }
}

/// <summary>Shared conversion rules for standalone deposit-fund movements.</summary>
public abstract class SecurityDepositSourceJournalGeneratorBase : AccountingSourceJournalGeneratorBase
{
    protected SecurityDepositSourceJournalGeneratorBase(
        RentalCommandDbContext db,
        JournalSourceType sourceType,
        params SecurityDepositEntryType[] normalTypes)
        : base(db, sourceType) => NormalTypes = normalTypes;

    private IReadOnlyList<SecurityDepositEntryType> NormalTypes { get; }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.SecurityDepositEntries
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.Id > afterSourceId
                && (NormalTypes.Contains(entry.EntryType)
                    || entry.EntryType == SecurityDepositEntryType.Reversal))
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var entry = await Db.SecurityDepositEntries
            .AsNoTracking()
            .Include(item => item.SecurityDepositAccount)
                .ThenInclude(account => account!.TenantAccount)
                    .ThenInclude(account => account!.LeaseManagement)
            .SingleOrDefaultAsync(item => item.PortfolioId == key.PortfolioId
                && item.Id == key.SourceId, ct);
        if (entry is null || entry.Amount <= 0m)
            return null;

        if (entry.ReversesEntryId is { } originalId)
        {
            var original = await Db.JournalEntries
                .Include(journal => journal.Lines)
                .SingleOrDefaultAsync(journal => journal.PortfolioId == key.PortfolioId
                    && journal.SourceType == SourceType
                    && journal.SourceId == originalId, ct);
            if (original is null)
                return null;
            return Proposal(
                key,
                entry.EffectiveOn,
                entry.Currency.ToUpperInvariant(),
                $"Reversal: {entry.Description}",
                string.IsNullOrWhiteSpace(entry.BusinessKey)
                    ? $"security-deposit-reversal:{entry.Id}"
                    : entry.BusinessKey,
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
                    SourceLineType = OppositeLineType(line.SourceLineType),
                    SourceLineId = line.SourceLineId,
                }),
                original.Id);
        }

        if (!NormalTypes.Contains(entry.EntryType))
            return null;
        // A deposit receipt paired to a tenant payment is already represented by that payment's
        // TenantReceipt journal. Posting the SecurityDepositEntry as a second cash movement would
        // double-count the deposit.
        if (SourceType == JournalSourceType.SecurityDepositReceipt
            && entry.TenantLedgerEntryId is not null)
            return null;

        var tenantAccount = entry.SecurityDepositAccount?.TenantAccount;
        var tenantAccountId = tenantAccount?.Id;
        var propertyId = tenantAccount?.LeaseManagement?.PropertyId;
        var unitId = tenantAccount?.LeaseManagement?.UnitId;
        var payable = await AccountAsync(key.PortfolioId, "tenant-security-deposits-payable", ct);
        var currency = entry.Currency.ToUpperInvariant();
        var lines = new List<AccountingProposedLine>(2);
        if (SourceType == JournalSourceType.SecurityDepositReceipt)
        {
            var trust = await AccountAsync(key.PortfolioId, "security-deposit-trust-cash", ct);
            lines.Add(Debit(trust, entry.Amount, "debit:security-deposit-trust-cash", entry.Description,
                entry.Id, propertyId, unitId, tenantAccountId));
            lines.Add(Credit(payable, entry.Amount, "credit:security-deposit-payable", entry.Description,
                entry.Id, propertyId, unitId, tenantAccountId));
        }
        else if (SourceType == JournalSourceType.SecurityDepositRefund)
        {
            var trust = await AccountAsync(key.PortfolioId, "security-deposit-trust-cash", ct);
            lines.Add(Debit(payable, entry.Amount, "debit:security-deposit-payable", entry.Description,
                entry.Id, propertyId, unitId, tenantAccountId));
            lines.Add(Credit(trust, entry.Amount, "credit:cash", entry.Description,
                entry.Id, propertyId, unitId, tenantAccountId));
        }
        else
        {
            var receivable = await AccountAsync(key.PortfolioId, "tenant-accounts-receivable", ct);
            lines.Add(Debit(payable, entry.Amount, "debit:security-deposit-payable", entry.Description,
                entry.Id, propertyId, unitId, tenantAccountId));
            lines.Add(Credit(receivable, entry.Amount, "credit:tenant-receivable", entry.Description,
                entry.Id, propertyId, unitId, tenantAccountId));
        }

        return Proposal(
            key,
            entry.EffectiveOn,
            currency,
            string.IsNullOrWhiteSpace(entry.Description) ? "Security deposit movement" : entry.Description,
            string.IsNullOrWhiteSpace(entry.BusinessKey)
                ? $"security-deposit:{entry.Id}"
                : entry.BusinessKey,
            lines);
    }

    public override async Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) =>
        await Db.SecurityDepositEntries
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.Currency == currency
                && NormalTypes.Contains(entry.EntryType)
                && entry.ReversesEntryId == null
                && !(SourceType == JournalSourceType.SecurityDepositReceipt
                    && entry.TenantLedgerEntryId != null))
            .Select(entry => (decimal?)entry.Amount)
            .SumAsync(ct) ?? 0m;

    private static string OppositeLineType(string? lineType) =>
        lineType?.StartsWith("debit:", StringComparison.Ordinal) == true
            ? "credit:" + lineType[6..]
            : lineType?.StartsWith("credit:", StringComparison.Ordinal) == true
                ? "debit:" + lineType[7..]
                : lineType ?? string.Empty;
}

public sealed class SecurityDepositReceiptSourceJournalGenerator : SecurityDepositSourceJournalGeneratorBase
{
    public SecurityDepositReceiptSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.SecurityDepositReceipt, SecurityDepositEntryType.Receipt) { }
}

public sealed class SecurityDepositRefundSourceJournalGenerator : SecurityDepositSourceJournalGeneratorBase
{
    public SecurityDepositRefundSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.SecurityDepositRefund, SecurityDepositEntryType.Refund) { }
}

public sealed class SecurityDepositApplicationSourceJournalGenerator : SecurityDepositSourceJournalGeneratorBase
{
    public SecurityDepositApplicationSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.SecurityDepositApplication,
            SecurityDepositEntryType.Deduction, SecurityDepositEntryType.Adjustment) { }
}

/// <summary>Converts an expense matched to a bank payment after its bill was incurred.</summary>
public sealed class BillPaymentSourceJournalGenerator : AccountingSourceJournalGeneratorBase
{
    public BillPaymentSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.BillPayment) { }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.BankTransactions
            .Where(transaction => transaction.PortfolioId == portfolioId
                && transaction.Id > afterSourceId
                && transaction.MatchedExpenseId != null
                && transaction.Amount < 0m
                && transaction.MatchStatus == "Matched")
            .OrderBy(transaction => transaction.Id)
            .Select(transaction => (long)transaction.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var transaction = await Db.BankTransactions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PortfolioId == key.PortfolioId
                && item.Id == key.SourceId, ct);
        if (transaction?.MatchedExpenseId is not { } expenseId || transaction.Amount >= 0m)
            return null;
        var expense = await Db.Expenses.AsNoTracking().SingleOrDefaultAsync(item =>
            item.PortfolioId == key.PortfolioId && item.Id == expenseId, ct);
        if (expense is null || expense.Status == ExpenseStatus.Paid || expense.PaidAt is not null)
            return null;

        var expenseAccount = await AccountAsync(
            key.PortfolioId, ExpensePaymentSourceJournalGenerator.ExpenseSystemKeyForConversion(expense.Category), ct);
        var payable = await AccountAsync(key.PortfolioId, "accounts-payable", ct);
        var cash = await AccountAsync(key.PortfolioId, "operating-cash", ct);
        var amount = Math.Abs(transaction.Amount);
        return Proposal(
            key,
            DateOnly.FromDateTime(transaction.PostedAt),
            transaction.IsoCurrencyCode.ToUpperInvariant(),
            $"Paid bill: {expense.Description}",
            $"bank-bill-payment:{transaction.Id}",
            [
                Debit(payable, amount, "debit:accounts-payable", expense.Description,
                    expense.Id, expense.PropertyId, expense.UnitId),
                Credit(cash, amount, "credit:operating-cash", expense.Description,
                    expense.Id, expense.PropertyId, expense.UnitId),
            ]);
    }

    public override async Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) =>
        await Db.BankTransactions
            .Where(transaction => transaction.PortfolioId == portfolioId
                && transaction.MatchedExpenseId != null
                && transaction.Amount < 0m
                && transaction.MatchStatus == "Matched"
                && transaction.IsoCurrencyCode == currency)
            .Select(transaction => (decimal?)Math.Abs(transaction.Amount))
            .SumAsync(ct) ?? 0m;
}

/// <summary>Converts provider receipts that were matched to an operating bank settlement.</summary>
public sealed class ProviderSettlementSourceJournalGenerator : AccountingSourceJournalGeneratorBase
{
    public ProviderSettlementSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.ProviderSettlement) { }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.BankTransactions
            .Where(transaction => transaction.PortfolioId == portfolioId
                && transaction.Id > afterSourceId
                && transaction.MatchedTenantLedgerEntryId != null
                && transaction.Amount > 0m
                && transaction.MatchStatus == "Matched"
                && Db.TenantLedgerEntries.Any(entry => entry.PortfolioId == portfolioId
                    && entry.Id == transaction.MatchedTenantLedgerEntryId
                    && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && entry.ProviderPaymentAttemptId != null
                    && Db.TenantPaymentAttempts.Any(attempt => attempt.PortfolioId == portfolioId
                        && attempt.Id == entry.ProviderPaymentAttemptId
                        && attempt.Provider != "manual")))
            .OrderBy(transaction => transaction.Id)
            .Select(transaction => (long)transaction.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var transaction = await Db.BankTransactions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.PortfolioId == key.PortfolioId && item.Id == key.SourceId, ct);
        if (transaction?.MatchedTenantLedgerEntryId is not { } receiptId || transaction.Amount <= 0m)
            return null;
        var receipt = await Db.TenantLedgerEntries.AsNoTracking().SingleOrDefaultAsync(entry =>
            entry.PortfolioId == key.PortfolioId
            && entry.Id == receiptId
            && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
            && entry.ProviderPaymentAttemptId != null, ct);
        if (receipt?.ProviderPaymentAttemptId is not { } attemptId)
            return null;
        var provider = await Db.TenantPaymentAttempts.AsNoTracking().SingleOrDefaultAsync(attempt =>
            attempt.PortfolioId == key.PortfolioId && attempt.Id == attemptId, ct);
        if (provider is null || string.Equals(provider.Provider, "manual", StringComparison.OrdinalIgnoreCase))
            return null;

        var cash = await AccountAsync(key.PortfolioId, "operating-cash", ct);
        var undeposited = await AccountAsync(key.PortfolioId, "undeposited-funds", ct);
        return Proposal(
            key,
            DateOnly.FromDateTime(transaction.PostedAt),
            transaction.IsoCurrencyCode.ToUpperInvariant(),
            $"Provider settlement: {transaction.Description}",
            $"provider-settlement:{transaction.ProviderTransactionId}",
            [
                Debit(cash, transaction.Amount, "debit:operating-cash", transaction.Description,
                    transaction.Id, transaction.PropertyId),
                Credit(undeposited, transaction.Amount, "credit:undeposited-funds", transaction.Description,
                    transaction.Id, transaction.PropertyId),
            ]);
    }

    public override async Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) =>
        await Db.BankTransactions
            .Where(transaction => transaction.PortfolioId == portfolioId
                && transaction.MatchedTenantLedgerEntryId != null
                && transaction.Amount > 0m
                && transaction.MatchStatus == "Matched"
                && transaction.IsoCurrencyCode == currency
                && Db.TenantLedgerEntries.Any(entry => entry.PortfolioId == portfolioId
                    && entry.Id == transaction.MatchedTenantLedgerEntryId
                    && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && entry.ProviderPaymentAttemptId != null
                    && Db.TenantPaymentAttempts.Any(attempt => attempt.PortfolioId == portfolioId
                        && attempt.Id == entry.ProviderPaymentAttemptId
                        && attempt.Provider != "manual")))
            .Select(transaction => (decimal?)transaction.Amount)
            .SumAsync(ct) ?? 0m;
}

/// <summary>
/// Bank transfers are intentionally reported as no accounting effect when both sides use the
/// current operating-cash mapping. They are not unsupported sources.
/// </summary>
public sealed class BankTransferSourceJournalGenerator : AccountingSourceJournalGeneratorBase
{
    public BankTransferSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.BankTransfer) { }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.BankTransactions
            .Where(transaction => transaction.PortfolioId == portfolioId
                && transaction.MatchedBankTransactionId != null
                && transaction.Id > transaction.MatchedBankTransactionId
                && (transaction.Id > afterSourceId
                    || transaction.MatchedBankTransactionId > afterSourceId))
            .Select(transaction => transaction.MatchedBankTransactionId < transaction.Id
                ? (long)transaction.MatchedBankTransactionId!.Value
                : transaction.Id)
            .Distinct()
            .OrderBy(id => id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var first = await Db.BankTransactions.AsNoTracking().SingleOrDefaultAsync(transaction =>
            transaction.PortfolioId == key.PortfolioId && transaction.Id == key.SourceId, ct);
        if (first?.MatchedBankTransactionId is not { } secondId)
            return null;
        var second = await Db.BankTransactions.AsNoTracking().SingleOrDefaultAsync(transaction =>
            transaction.PortfolioId == key.PortfolioId && transaction.Id == secondId, ct);
        if (second is null
            || first.Amount == 0m
            || second.Amount == 0m
            || Math.Sign(first.Amount) == Math.Sign(second.Amount)
            || Math.Abs(first.Amount) != Math.Abs(second.Amount)
            || !string.Equals(first.IsoCurrencyCode, second.IsoCurrencyCode, StringComparison.OrdinalIgnoreCase))
            return null;

        // There is one operating-cash system mapping in the foundation chart. A transfer between
        // two statement rows under that same account has no general-ledger consequence.
        return null;
    }

    public override Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) => Task.FromResult(0m);
}

/// <summary>Converts paid mortgage payments with the persisted principal/interest/escrow split.</summary>
public sealed class LoanPaymentSourceJournalGenerator : AccountingSourceJournalGeneratorBase
{
    public LoanPaymentSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.LoanPayment) { }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.LoanPayments
            .Where(payment => payment.PortfolioId == portfolioId
                && payment.Id > afterSourceId
                && payment.Status == LoanPaymentStatus.Paid)
            .OrderBy(payment => payment.Id)
            .Select(payment => (long)payment.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var payment = await Db.LoanPayments.AsNoTracking()
            .Include(item => item.Loan)
            .SingleOrDefaultAsync(item => item.PortfolioId == key.PortfolioId
                && item.Id == key.SourceId, ct);
        if (payment is null || payment.Status != LoanPaymentStatus.Paid || payment.TotalAmount <= 0m)
            return null;

        var latest = await Db.LoanPaymentCorrections.AsNoTracking()
            .Where(correction => correction.PortfolioId == key.PortfolioId
                && correction.LoanPaymentId == payment.Id)
            .OrderByDescending(correction => correction.CreatedAtUtc)
            .ThenByDescending(correction => correction.Id)
            .Select(correction => new
            {
                correction.DueDate,
                correction.PaidDate,
                correction.InterestAmount,
                correction.PrincipalAmount,
                correction.EscrowAmount,
                correction.TotalAmount,
                correction.Status,
            })
            .FirstOrDefaultAsync(ct);
        var principalAmount = latest?.PrincipalAmount ?? payment.PrincipalAmount;
        var interestAmount = latest?.InterestAmount ?? payment.InterestAmount;
        var escrowAmount = latest?.EscrowAmount ?? payment.EscrowAmount;
        var totalAmount = latest?.TotalAmount ?? payment.TotalAmount;
        var paidDate = latest?.PaidDate ?? payment.PaidDate;
        if (latest?.Status == LoanPaymentStatus.Scheduled || totalAmount <= 0m)
            return null;

        var principal = await AccountAsync(key.PortfolioId, "mortgage-payable", ct);
        var interest = await AccountAsync(key.PortfolioId, "mortgage-interest", ct);
        var escrow = await AccountAsync(key.PortfolioId, "mortgage-escrow-asset", ct);
        var cash = await AccountAsync(key.PortfolioId, "operating-cash", ct);
        var lines = new List<AccountingProposedLine>(4);
        if (principalAmount > 0m)
            lines.Add(Debit(principal, principalAmount, "debit:mortgage-principal", "Mortgage principal",
                payment.Id, payment.Loan?.PropertyId));
        if (interestAmount > 0m)
            lines.Add(Debit(interest, interestAmount, "debit:mortgage-interest", "Mortgage interest",
                payment.Id, payment.Loan?.PropertyId));
        if (escrowAmount > 0m)
            lines.Add(Debit(escrow, escrowAmount, "debit:mortgage-escrow", "Mortgage escrow",
                payment.Id, payment.Loan?.PropertyId));
        lines.Add(Credit(cash, totalAmount, "credit:operating-cash", "Mortgage payment",
            payment.Id, payment.Loan?.PropertyId));
        return Proposal(
            key,
            DateOnly.FromDateTime(paidDate ?? payment.DueDate),
            await PortfolioCurrencyAsync(key.PortfolioId, ct),
            "Mortgage payment posted",
            latest is null ? $"loan-payment:{payment.Id}" : $"loan-payment-correction:{payment.Id}",
            lines);
    }

    public override async Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) =>
        await Db.LoanPayments
            .Where(payment => payment.PortfolioId == portfolioId
                && payment.Status == LoanPaymentStatus.Paid)
            .Select(payment => (decimal?)payment.TotalAmount)
            .SumAsync(ct) ?? 0m;
}

/// <summary>Converts capital asset acquisitions and suppresses the replaced expense journals.</summary>
public sealed class CapitalPurchaseSourceJournalGenerator : AccountingSourceJournalGeneratorBase
{
    public CapitalPurchaseSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.CapitalPurchase) { }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.CapitalAssets
            .Where(asset => asset.PortfolioId == portfolioId
                && asset.Id > afterSourceId
                && asset.CostBasis > 0m)
            .OrderBy(asset => asset.Id)
            .Select(asset => (long)asset.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var asset = await Db.CapitalAssets.AsNoTracking().SingleOrDefaultAsync(item =>
            item.PortfolioId == key.PortfolioId && item.Id == key.SourceId, ct);
        if (asset is null || asset.CostBasis <= 0m)
            return null;
        var fundingKey = "operating-cash";
        if (asset.SourceExpenseId is { } expenseId)
        {
            var paid = await Db.Expenses
                .Where(expense => expense.PortfolioId == key.PortfolioId && expense.Id == expenseId)
                .Select(expense => expense.Status == ExpenseStatus.Paid || expense.PaidAt != null)
                .SingleOrDefaultAsync(ct);
            fundingKey = paid ? "operating-cash" : "accounts-payable";
        }
        var basis = await AccountAsync(key.PortfolioId, "buildings-and-improvements", ct);
        var funding = await AccountAsync(key.PortfolioId, fundingKey, ct);
        return Proposal(
            key,
            DateOnly.FromDateTime(asset.InServiceDate),
            await PortfolioCurrencyAsync(key.PortfolioId, ct),
            $"Capital purchase: {asset.Description}",
            $"capital-purchase:{asset.Id}",
            [
                Debit(basis, asset.CostBasis, "debit:asset", asset.Description, asset.Id,
                    asset.PropertyId, asset.UnitId),
                Credit(funding, asset.CostBasis, $"credit:{fundingKey}", asset.Description, asset.Id,
                    asset.PropertyId, asset.UnitId),
            ]);
    }

    public override async Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) =>
        await Db.CapitalAssets
            .Where(asset => asset.PortfolioId == portfolioId && asset.CostBasis > 0m)
            .Select(asset => (decimal?)asset.CostBasis)
            .SumAsync(ct) ?? 0m;
}

/// <summary>Converts approved owner distributions with the owner dimension attached to both lines.</summary>
public sealed class OwnerDistributionSourceJournalGenerator : AccountingSourceJournalGeneratorBase
{
    public OwnerDistributionSourceJournalGenerator(RentalCommandDbContext db)
        : base(db, JournalSourceType.OwnerDistribution) { }

    public override async Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default)
    {
        var ids = await Db.OwnerDistributions
            .Where(distribution => distribution.PortfolioId == portfolioId
                && distribution.Id > afterSourceId
                && distribution.Status == OwnerDistributionStatus.Approved
                && distribution.DeletedAt == null
                && distribution.Amount > 0m)
            .OrderBy(distribution => distribution.Id)
            .Select(distribution => (long)distribution.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        return ids;
    }

    public override async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        var distribution = await Db.OwnerDistributions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.PortfolioId == key.PortfolioId && item.Id == key.SourceId, ct);
        if (distribution is null
            || distribution.Status != OwnerDistributionStatus.Approved
            || distribution.DeletedAt is not null
            || distribution.Amount <= 0m)
            return null;
        var equity = await AccountAsync(key.PortfolioId, "owner-distributions", ct);
        var cash = await AccountAsync(key.PortfolioId, "operating-cash", ct);
        var memo = string.IsNullOrWhiteSpace(distribution.Memo)
            ? "Owner distribution"
            : distribution.Memo;
        return Proposal(
            key,
            DateOnly.FromDateTime(distribution.Date),
            await PortfolioCurrencyAsync(key.PortfolioId, ct),
            "Owner distribution paid",
            $"owner-distribution:{distribution.Id}",
            [
                Debit(equity, distribution.Amount, "debit:owner-distribution", memo, distribution.Id,
                    distribution.PropertyId, ownerEntityId: distribution.OwnerEntityId),
                Credit(cash, distribution.Amount, "credit:operating-cash", memo, distribution.Id,
                    distribution.PropertyId, ownerEntityId: distribution.OwnerEntityId),
            ]);
    }

    public override async Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        string currency,
        CancellationToken ct = default) =>
        await Db.OwnerDistributions
            .Where(distribution => distribution.PortfolioId == portfolioId
                && distribution.Status == OwnerDistributionStatus.Approved
                && distribution.DeletedAt == null)
            .Select(distribution => (decimal?)distribution.Amount)
            .SumAsync(ct) ?? 0m;
}

/// <summary>
/// Conversion proposals need the same actor/attempt fields as live atomic commands while they do
/// not have a user request. This context is intentionally limited to deterministic metadata.
/// </summary>
internal sealed class ConversionAtomicCommandContext : IAtomicCommandContext
{
    public ConversionAtomicCommandContext(AccountingSourceJournalKey key)
    {
        AttemptId = StableGuid(key, "attempt");
        AtomicReceiptId = StableGuid(key, "receipt");
    }

    public bool IsActive => true;
    public Guid AttemptId { get; }
    public Guid AtomicReceiptId { get; }
    public DateTime BusinessNowUtc => DateTime.UtcNow;

    public Task<DateTime> ReadDatabaseClockUtcAsync(CancellationToken ct = default) =>
        Task.FromResult(DateTime.UtcNow);

    public Task AcquireLockAsync(string lockNamespace, int aggregateId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task AcquireLockAsync(string lockNamespace, Guid aggregateId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<AtomicBusinessFlush> FlushBusinessAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Historical conversion does not flush business rows.");

    public void BindSemanticAudit(object entityReference, AtomicSemanticAudit audit) =>
        throw new NotSupportedException("Historical conversion does not write semantic audits.");

    public void UseDatabaseWallClockForAudit(DateTime occurredAtUtc) { }

    public void EnrichMutation(AtomicAuditMutation mutation, AtomicSemanticAudit audit) { }

    public void StageSemanticEvent(AtomicSemanticAudit audit) =>
        throw new NotSupportedException("Historical conversion does not stage command audits.");

    public void StageSemanticEvent(AtomicSemanticAudit audit, DateTime occurredAtUtc) =>
        throw new NotSupportedException("Historical conversion does not stage command audits.");

    public void StageOutbox(OutboxMessage message) =>
        throw new NotSupportedException("Historical conversion does not stage outbox messages.");

    private static Guid StableGuid(AccountingSourceJournalKey key, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{key.PortfolioId}:{key.SourceType}:{key.SourceId}:{key.PostingRuleVersion}:{purpose}"));
        return new Guid(bytes[..16]);
    }
}
