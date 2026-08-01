using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data.Accounting;

namespace RentalCommand.Data.Payments;

/// <summary>Builds the locked tenant and deposit posting rules inside the owning atomic command.</summary>
internal static class TenantAccountingPosting
{
    private const int PostingRuleVersion = 1;
    private const string Receivable = "tenant-accounts-receivable";
    private const string OperatingCash = "operating-cash";
    private const string TrustCash = "security-deposit-trust-cash";
    private const string DepositPayable = "tenant-security-deposits-payable";

    internal static Task<JournalEntry> PostTenantChargeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry charge,
        int actorUserId,
        string? incomeSystemKey = null,
        CancellationToken ct = default,
        string? actorLabel = null) =>
        charge.EntryType == TenantLedgerEntryType.DepositCharge
            ? PostDepositChargeAsync(db, context, charge, actorUserId, actorLabel, ct)
            : PostIncomeEntryAsync(
                db,
                context,
                charge,
                JournalSourceType.TenantCharge,
                incomeSystemKey ?? IncomeSystemKey(charge.EntryType),
                debitReceivable: true,
                actorUserId,
                actorLabel,
                ct);

    internal static Task<JournalEntry> PostTenantChargeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry charge,
        int actorUserId,
        int? incomeLedgerAccountId,
        CancellationToken ct = default,
        string? actorLabel = null) =>
        charge.EntryType == TenantLedgerEntryType.DepositCharge
            ? PostDepositChargeAsync(db, context, charge, actorUserId, actorLabel, ct)
            : PostIncomeEntryAsync(
                db,
                context,
                charge,
                JournalSourceType.TenantCharge,
                IncomeSystemKey(charge.EntryType),
                debitReceivable: true,
                actorUserId,
                actorLabel,
                ct,
                incomeLedgerAccountId,
                requireActiveIncomeAccount: true);

    /// <summary>
    /// Posts scheduled rent and late-fee charges with one system-account lookup and one posting
    /// preflight, rather than repeating account and idempotency reads for every occurrence.
    /// </summary>
    internal static async Task<IReadOnlyList<JournalEntry>> PostScheduledTenantChargesAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        IReadOnlyCollection<TenantLedgerEntry> entries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return Array.Empty<JournalEntry>();

        var portfolioId = entries.First().PortfolioId;
        if (entries.Any(entry => entry.PortfolioId != portfolioId))
        {
            throw new AccountingPostingValidationException(
                "Scheduled tenant charges must belong to one portfolio.");
        }

        var incomeKeys = entries.Select(entry => entry.EntryType switch
        {
            TenantLedgerEntryType.RentCharge => "rental-income",
            TenantLedgerEntryType.LateFeeCharge => "late-fee-income",
            _ => throw new AccountingPostingValidationException(
                "Only scheduled rent and late-fee charges can use the batch posting rule."),
        }).Distinct().ToArray();
        var systemKeys = new[] { Receivable }.Concat(incomeKeys).ToArray();
        var accountRows = await db.LedgerAccounts.AsNoTracking()
            .Where(account => account.PortfolioId == portfolioId
                && account.IsSystem
                && account.IsActive
                && account.SystemKey != null
                && systemKeys.Contains(account.SystemKey))
            .Select(account => new { account.SystemKey, account.Id })
            .ToListAsync(ct);
        var accounts = accountRows.ToDictionary(row => row.SystemKey!, row => row.Id, StringComparer.Ordinal);
        if (!accounts.TryGetValue(Receivable, out var receivable)
            || incomeKeys.Any(key => !accounts.ContainsKey(key)))
        {
            throw new AccountingPostingValidationException(
                "One or more required accounting mappings are missing or inactive.");
        }

        var proposals = entries.Select(entry =>
        {
            var incomeKey = entry.EntryType == TenantLedgerEntryType.LateFeeCharge
                ? "late-fee-income"
                : "rental-income";
            return AccountingPostingSupport.BuildProposal(
                context,
                entry.PortfolioId,
                JournalSourceType.TenantCharge,
                entry.Id,
                entry.BusinessKey,
                PostingRuleVersion,
                entry.EffectiveOn,
                entry.Currency,
                entry.Description,
                [
                    Debit(receivable, entry.Amount, "debit:tenant-receivable", entry),
                    Credit(accounts[incomeKey], entry.Amount, "credit:income", entry),
                ],
                userId: entry.CreatedByUserId > 0 ? entry.CreatedByUserId : null,
                actorLabel: entry.CreatedByUserId > 0
                    ? null
                    : "system:scheduled-tenant-billing");
        }).ToArray();

        return await new AccountingPostingService(db).PostBatchAsync(proposals, ct);
    }

    /// <summary>
    /// Posts the native e-sign execution deposit charges with one account-map read and one
    /// posting preflight for the complete claimed batch. The worker can span portfolios, so the
    /// map is keyed by both portfolio and system account.
    /// </summary>
    internal static async Task<IReadOnlyList<JournalEntry>> PostInitialSecurityDepositChargesAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        IReadOnlyCollection<TenantLedgerEntry> entries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return Array.Empty<JournalEntry>();
        if (entries.Any(entry => entry.EntryType != TenantLedgerEntryType.DepositCharge))
        {
            throw new AccountingPostingValidationException(
                "Only security deposit charges can use the native e-sign batch posting rule.");
        }

        var portfolioIds = entries.Select(entry => entry.PortfolioId).Distinct().ToArray();
        var systemKeys = new[] { Receivable, DepositPayable };
        var accountRows = await db.LedgerAccounts.AsNoTracking()
            .Where(account => portfolioIds.Contains(account.PortfolioId)
                && account.IsSystem
                && account.IsActive
                && account.SystemKey != null
                && systemKeys.Contains(account.SystemKey))
            .Select(account => new { account.PortfolioId, account.SystemKey, account.Id })
            .ToListAsync(ct);
        var accounts = accountRows.ToDictionary(
            row => new SystemAccountKey(row.PortfolioId, row.SystemKey!),
            row => row.Id);
        var proposals = new List<AccountingProposedEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (!accounts.TryGetValue(new SystemAccountKey(entry.PortfolioId, Receivable), out var receivable)
                || !accounts.TryGetValue(new SystemAccountKey(entry.PortfolioId, DepositPayable), out var payable))
            {
                throw new AccountingPostingValidationException(
                    "One or more required accounting mappings are missing or inactive.");
            }

            proposals.Add(AccountingPostingSupport.BuildProposal(
                context,
                entry.PortfolioId,
                JournalSourceType.TenantCharge,
                entry.Id,
                entry.BusinessKey,
                PostingRuleVersion,
                entry.EffectiveOn,
                entry.Currency,
                entry.Description,
                [
                    Debit(receivable, entry.Amount, "debit:tenant-receivable", entry),
                    Credit(payable, entry.Amount, "credit:security-deposit-payable", entry),
                ],
                userId: entry.CreatedByUserId > 0 ? entry.CreatedByUserId : null,
                actorLabel: "native-esign"));
        }

        return await new AccountingPostingService(db).PostBatchAsync(proposals, ct);
    }

    internal static Task<JournalEntry> PostTenantConcessionAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry entry,
        int actorUserId,
        CancellationToken ct = default) =>
        PostIncomeEntryAsync(
            db,
            context,
            entry,
            JournalSourceType.TenantConcession,
            IncomeSystemKey(entry.EntryType),
            debitReceivable: entry.Direction == TenantLedgerDirection.Debit,
            actorUserId,
            actorLabel: null,
            ct);

    internal static async Task<JournalEntry> PostTenantConcessionAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry entry,
        int actorUserId,
        int? incomeLedgerAccountId,
        CancellationToken ct = default)
    {
        var targetedIncomeAccountId = entry.RelatedTenantLedgerEntryId is long originalChargeEntryId
            ? await FindOriginalIncomeAccountIdAsync(
                db, entry.PortfolioId, originalChargeEntryId, ct)
            : incomeLedgerAccountId;
        return await PostIncomeEntryAsync(
            db,
            context,
            entry,
            JournalSourceType.TenantConcession,
            IncomeSystemKey(entry.EntryType),
            debitReceivable: false,
            actorUserId,
            actorLabel: null,
            ct,
            targetedIncomeAccountId,
            requireActiveIncomeAccount: entry.RelatedTenantLedgerEntryId is null);
    }

    internal static async Task<JournalEntry> PostTenantReceiptAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry receipt,
        int actorUserId,
        string cashSystemKey = OperatingCash,
        CancellationToken ct = default)
    {
        var cash = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, receipt.PortfolioId, cashSystemKey, ct);
        var receivable = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, receipt.PortfolioId, Receivable, ct);
        var proposal = AccountingPostingSupport.BuildProposal(
            context,
            receipt.PortfolioId,
            JournalSourceType.TenantReceipt,
            receipt.Id,
            receipt.BusinessKey,
            PostingRuleVersion,
            receipt.EffectiveOn,
            receipt.Currency,
            receipt.Description,
            [
                Debit(cash, receipt.Amount, CashSourceLineType(cashSystemKey), receipt),
                Credit(receivable, receipt.Amount, "credit:tenant-receivable", receipt),
            ],
            userId: actorUserId);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    internal static async Task<JournalEntry> PostTenantRefundAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry refund,
        long originalReceiptId,
        int actorUserId,
        CancellationToken ct = default)
    {
        var original = await FindRequiredJournalAsync(
            db, refund.PortfolioId, JournalSourceType.TenantReceipt, originalReceiptId, ct);
        return await PostOppositeAsync(
            db,
            context,
            refund,
            JournalSourceType.TenantReceipt,
            original,
            actorUserId,
            ct);
    }

    internal static async Task<JournalEntry> PostTenantReversalAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry reversal,
        TenantLedgerEntry originalEntry,
        int actorUserId,
        CancellationToken ct = default)
    {
        var sourceType = SourceTypeForTenantEntry(originalEntry.EntryType);
        var original = await FindRequiredJournalAsync(
            db, reversal.PortfolioId, sourceType, originalEntry.Id, ct);
        return await PostOppositeAsync(
            db,
            context,
            reversal,
            sourceType,
            original,
            actorUserId,
            ct);
    }

    private static async Task<JournalEntry> PostOppositeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        SecurityDepositEntry reversal,
        JournalSourceType sourceType,
        JournalEntry original,
        int actorUserId,
        CancellationToken ct)
    {
        var lines = original.Lines.Select(line => new AccountingProposedLine
        {
            LedgerAccountId = line.LedgerAccountId,
            DebitAmount = line.CreditAmount,
            CreditAmount = line.DebitAmount,
            Memo = line.Memo,
            PropertyId = line.PropertyId,
            UnitId = line.UnitId,
            TenantAccountId = line.TenantAccountId,
            OwnerEntityId = line.OwnerEntityId,
            SourceLineType = line.SourceLineType,
            SourceLineId = line.SourceLineId,
        });
        var sourceId = sourceType == JournalSourceType.TenantReceipt
            ? reversal.TenantLedgerEntryId
            : reversal.Id;
        if (sourceId is null or <= 0)
            throw new AccountingPostingValidationException(
                "The deposit reversal is missing its tenant-ledger source identity.");
        var proposal = AccountingPostingSupport.BuildProposal(
            context,
            reversal.PortfolioId,
            sourceType,
            sourceId.Value,
            reversal.BusinessKey,
            PostingRuleVersion,
            reversal.EffectiveOn,
            reversal.Currency,
            reversal.Description,
            lines,
            original.Id,
            userId: actorUserId);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    internal static async Task<JournalEntry> PostSecurityDepositReceiptAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        SecurityDepositEntry deposit,
        int actorUserId,
        CancellationToken ct = default)
    {
        var trustCash = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, deposit.PortfolioId, TrustCash, ct);
        var payable = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, deposit.PortfolioId, DepositPayable, ct);
        var tenantAccountId = await ResolveTenantAccountIdAsync(db, deposit, ct);
        var proposal = AccountingPostingSupport.BuildProposal(
            context,
            deposit.PortfolioId,
            JournalSourceType.SecurityDepositReceipt,
            deposit.Id,
            deposit.BusinessKey,
            PostingRuleVersion,
            deposit.EffectiveOn,
            deposit.Currency,
            deposit.Description,
            [
                new AccountingProposedLine
                {
                    LedgerAccountId = trustCash,
                    DebitAmount = deposit.Amount,
                    TenantAccountId = tenantAccountId,
                    SourceLineType = "debit:security-deposit-trust-cash",
                    SourceLineId = deposit.Id,
                    Memo = deposit.Description,
                },
                new AccountingProposedLine
                {
                    LedgerAccountId = payable,
                    CreditAmount = deposit.Amount,
                    TenantAccountId = tenantAccountId,
                    SourceLineType = "credit:security-deposit-payable",
                    SourceLineId = deposit.Id,
                    Memo = deposit.Description,
                },
            ],
            userId: actorUserId);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    internal static async Task<JournalEntry> PostSecurityDepositRefundAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        SecurityDepositEntry deposit,
        int actorUserId,
        CancellationToken ct = default)
    {
        var payable = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, deposit.PortfolioId, DepositPayable, ct);
        var trustCash = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, deposit.PortfolioId, TrustCash, ct);
        var tenantAccountId = await ResolveTenantAccountIdAsync(db, deposit, ct);
        var proposal = AccountingPostingSupport.BuildProposal(
            context,
            deposit.PortfolioId,
            JournalSourceType.SecurityDepositRefund,
            deposit.Id,
            deposit.BusinessKey,
            PostingRuleVersion,
            deposit.EffectiveOn,
            deposit.Currency,
            deposit.Description,
            [
                new AccountingProposedLine
                {
                    LedgerAccountId = payable,
                    DebitAmount = deposit.Amount,
                    TenantAccountId = tenantAccountId,
                    SourceLineType = "debit:security-deposit-payable",
                    SourceLineId = deposit.Id,
                    Memo = deposit.Description,
                },
                new AccountingProposedLine
                {
                    LedgerAccountId = trustCash,
                    CreditAmount = deposit.Amount,
                    TenantAccountId = tenantAccountId,
                    SourceLineType = "credit:cash",
                    SourceLineId = deposit.Id,
                    Memo = deposit.Description,
                },
            ],
            userId: actorUserId);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    internal static async Task<JournalEntry> PostSecurityDepositApplicationAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        SecurityDepositEntry deposit,
        int actorUserId,
        CancellationToken ct = default)
    {
        var payable = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, deposit.PortfolioId, DepositPayable, ct);
        var receivable = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, deposit.PortfolioId, Receivable, ct);
        var tenantAccountId = await ResolveTenantAccountIdAsync(db, deposit, ct);
        var proposal = AccountingPostingSupport.BuildProposal(
            context,
            deposit.PortfolioId,
            JournalSourceType.SecurityDepositApplication,
            deposit.Id,
            deposit.BusinessKey,
            PostingRuleVersion,
            deposit.EffectiveOn,
            deposit.Currency,
            deposit.Description,
            [
                new AccountingProposedLine
                {
                    LedgerAccountId = payable,
                    DebitAmount = deposit.Amount,
                    TenantAccountId = tenantAccountId,
                    SourceLineType = "debit:security-deposit-payable",
                    SourceLineId = deposit.Id,
                    Memo = deposit.Description,
                },
                new AccountingProposedLine
                {
                    LedgerAccountId = receivable,
                    CreditAmount = deposit.Amount,
                    TenantAccountId = tenantAccountId,
                    SourceLineType = "credit:tenant-receivable",
                    SourceLineId = deposit.Id,
                    Memo = deposit.Description,
                },
            ],
            userId: actorUserId);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    internal static async Task<JournalEntry> PostSecurityDepositReversalAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        SecurityDepositEntry reversal,
        SecurityDepositEntry originalEntry,
        int actorUserId,
        CancellationToken ct = default)
    {
        var sourceType = originalEntry.EntryType == SecurityDepositEntryType.Receipt
            && originalEntry.TenantLedgerEntryId is not null
            ? JournalSourceType.TenantReceipt
            : SourceTypeForDepositEntry(originalEntry.EntryType);
        var sourceId = sourceType == JournalSourceType.TenantReceipt
            ? originalEntry.TenantLedgerEntryId!.Value
            : originalEntry.Id;
        var original = await FindRequiredJournalAsync(
            db, reversal.PortfolioId, sourceType, sourceId, ct);
        return await PostOppositeAsync(
            db,
            context,
            reversal,
            sourceType,
            original,
            actorUserId,
            ct);
    }

    private static async Task<JournalEntry> PostIncomeEntryAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry entry,
        JournalSourceType sourceType,
        string incomeSystemKey,
        bool debitReceivable,
        int actorUserId,
        string? actorLabel,
        CancellationToken ct,
        int? incomeLedgerAccountId = null,
        bool requireActiveIncomeAccount = true)
    {
        var receivable = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, entry.PortfolioId, Receivable, ct);
        var income = incomeLedgerAccountId is int selectedAccountId
            ? await RequireIncomeAccountIdAsync(
                db, entry.PortfolioId, selectedAccountId, requireActiveIncomeAccount, ct)
            : await AccountingPostingSupport.RequireSystemAccountIdAsync(
                db, entry.PortfolioId, incomeSystemKey, ct);
        var lines = debitReceivable
            ? new[]
            {
                Debit(receivable, entry.Amount, "debit:tenant-receivable", entry),
                Credit(income, entry.Amount, "credit:income", entry),
            }
            : new[]
            {
                Debit(income, entry.Amount, "debit:income", entry),
                Credit(receivable, entry.Amount, "credit:tenant-receivable", entry),
            };
        var proposal = AccountingPostingSupport.BuildProposal(
            context,
            entry.PortfolioId,
            sourceType,
            entry.Id,
            entry.BusinessKey,
            PostingRuleVersion,
            entry.EffectiveOn,
            entry.Currency,
            entry.Description,
            lines,
            userId: actorUserId > 0 ? actorUserId : null,
            actorLabel: actorLabel);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    private static async Task<int> RequireIncomeAccountIdAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int incomeLedgerAccountId,
        bool requireActive,
        CancellationToken ct)
    {
        var accountId = await db.LedgerAccounts
            .AsNoTracking()
            .Where(account => account.Id == incomeLedgerAccountId
                && account.PortfolioId == portfolioId
                && account.AccountType == AccountType.Income
                && (!requireActive || account.IsActive))
            .Select(account => (int?)account.Id)
            .SingleOrDefaultAsync(ct);
        return accountId ?? throw new AccountingPostingValidationException(
            "The selected income ledger account is not valid for this portfolio.");
    }

    private static async Task<int> FindOriginalIncomeAccountIdAsync(
        RentalCommandDbContext db,
        int portfolioId,
        long originalChargeEntryId,
        CancellationToken ct)
    {
        var accountId = await (
            from journal in db.JournalEntries.AsNoTracking()
            from line in journal.Lines
            join account in db.LedgerAccounts.AsNoTracking()
                on line.LedgerAccountId equals account.Id
            where journal.PortfolioId == portfolioId
                && journal.SourceType == JournalSourceType.TenantCharge
                && journal.SourceId == originalChargeEntryId
                && account.PortfolioId == portfolioId
                && account.AccountType == AccountType.Income
                && line.CreditAmount > 0m
            select (int?)line.LedgerAccountId).SingleOrDefaultAsync(ct);
        return accountId ?? throw new AccountingPostingValidationException(
            "The original charge journal has no positive income line.");
    }

    private static async Task<JournalEntry> PostDepositChargeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry entry,
        int actorUserId,
        string? actorLabel,
        CancellationToken ct)
    {
        var receivable = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, entry.PortfolioId, Receivable, ct);
        var payable = await AccountingPostingSupport.RequireSystemAccountIdAsync(
            db, entry.PortfolioId, DepositPayable, ct);
        var proposal = AccountingPostingSupport.BuildProposal(
            context,
            entry.PortfolioId,
            JournalSourceType.TenantCharge,
            entry.Id,
            entry.BusinessKey,
            PostingRuleVersion,
            entry.EffectiveOn,
            entry.Currency,
            entry.Description,
            [
                Debit(receivable, entry.Amount, "debit:tenant-receivable", entry),
                Credit(payable, entry.Amount, "credit:security-deposit-payable", entry),
            ],
            userId: actorUserId > 0 ? actorUserId : null,
            actorLabel: actorLabel);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    private static async Task<JournalEntry> PostOppositeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        TenantLedgerEntry reversal,
        JournalSourceType sourceType,
        JournalEntry original,
        int actorUserId,
        CancellationToken ct)
    {
        var lines = original.Lines.Select(line => new AccountingProposedLine
        {
            LedgerAccountId = line.LedgerAccountId,
            DebitAmount = line.CreditAmount,
            CreditAmount = line.DebitAmount,
            Memo = line.Memo,
            PropertyId = line.PropertyId,
            UnitId = line.UnitId,
            TenantAccountId = line.TenantAccountId,
            OwnerEntityId = line.OwnerEntityId,
            SourceLineType = line.SourceLineType,
            SourceLineId = line.SourceLineId,
        });
        var proposal = AccountingPostingSupport.BuildProposal(
            context,
            reversal.PortfolioId,
            sourceType,
            reversal.Id,
            reversal.BusinessKey,
            PostingRuleVersion,
            reversal.EffectiveOn,
            reversal.Currency,
            reversal.Description,
            lines,
            original.Id,
            userId: actorUserId);
        return await new AccountingPostingService(db).PostAsync(proposal, ct);
    }

    private static async Task<JournalEntry> FindRequiredJournalAsync(
        RentalCommandDbContext db,
        int portfolioId,
        JournalSourceType sourceType,
        long sourceId,
        CancellationToken ct) =>
        await db.JournalEntries
            .Include(entry => entry.Lines)
            .SingleOrDefaultAsync(entry => entry.PortfolioId == portfolioId
                && entry.SourceType == sourceType
                && entry.SourceId == sourceId, ct)
        ?? throw new AccountingPostingValidationException(
            "The original accounting entry is missing for this reversal.");

    private static AccountingProposedLine Debit(
        int accountId,
        decimal amount,
        string sourceLineType,
        TenantLedgerEntry entry) => new()
        {
            LedgerAccountId = accountId,
            DebitAmount = amount,
            TenantAccountId = entry.TenantAccountId,
            SourceLineType = sourceLineType,
            SourceLineId = entry.Id,
            Memo = entry.Description,
        };

    private static AccountingProposedLine Credit(
        int accountId,
        decimal amount,
        string sourceLineType,
        TenantLedgerEntry entry) => new()
        {
            LedgerAccountId = accountId,
            CreditAmount = amount,
            TenantAccountId = entry.TenantAccountId,
            SourceLineType = sourceLineType,
            SourceLineId = entry.Id,
            Memo = entry.Description,
        };

    private static string IncomeSystemKey(TenantLedgerEntryType entryType) =>
        entryType switch
        {
            TenantLedgerEntryType.LateFeeCharge => "late-fee-income",
            TenantLedgerEntryType.AddendumCharge => "other-rental-income",
            _ => "rental-income",
        };

    private static string CashSourceLineType(string cashSystemKey) =>
        string.Equals(cashSystemKey, TrustCash, StringComparison.Ordinal)
            ? "debit:security-deposit-trust-cash"
            : string.Equals(cashSystemKey, "undeposited-funds", StringComparison.Ordinal)
                ? "debit:undeposited-funds"
                : "debit:operating-cash";

    private readonly record struct SystemAccountKey(int PortfolioId, string SystemKey);

    private static JournalSourceType SourceTypeForTenantEntry(TenantLedgerEntryType entryType) =>
        entryType switch
        {
            TenantLedgerEntryType.RentCharge
                or TenantLedgerEntryType.AddendumCharge
                or TenantLedgerEntryType.LateFeeCharge
                or TenantLedgerEntryType.DepositCharge
                or TenantLedgerEntryType.ManualCharge => JournalSourceType.TenantCharge,
            TenantLedgerEntryType.Credit
                or TenantLedgerEntryType.Adjustment => JournalSourceType.TenantConcession,
            TenantLedgerEntryType.OpeningBalance => JournalSourceType.OpeningBalance,
            _ => throw new AccountingPostingValidationException(
                "This tenant ledger entry does not have a reversible accounting source."),
        };

    private static JournalSourceType SourceTypeForDepositEntry(SecurityDepositEntryType entryType) =>
        entryType switch
        {
            SecurityDepositEntryType.Receipt => JournalSourceType.SecurityDepositReceipt,
            SecurityDepositEntryType.Refund => JournalSourceType.SecurityDepositRefund,
            SecurityDepositEntryType.Deduction
                or SecurityDepositEntryType.Adjustment => JournalSourceType.SecurityDepositApplication,
            _ => throw new AccountingPostingValidationException(
                "This security-deposit entry does not have a reversible accounting source."),
        };

    private static Task<int?> ResolveTenantAccountIdAsync(
        RentalCommandDbContext db,
        SecurityDepositEntry entry,
        CancellationToken ct) =>
        entry.TenantLedgerEntryId is not long ledgerEntryId
            ? Task.FromResult<int?>(null)
            : db.TenantLedgerEntries
                .Where(ledger => ledger.PortfolioId == entry.PortfolioId && ledger.Id == ledgerEntryId)
                .Select(ledger => (int?)ledger.TenantAccountId)
                .SingleOrDefaultAsync(ct);

}
