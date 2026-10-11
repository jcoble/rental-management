using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Data.Accounting;

/// <summary>Shared account lookup, line ordering, and proposal construction for source commands.</summary>
public static class AccountingPostingSupport
{
    public static Task<int> RequireSystemAccountIdAsync(
        RentalCommandDbContext db,
        int portfolioId,
        string systemKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemKey);

        return RequireSystemAccountIdCoreAsync(db, portfolioId, systemKey, ct);
    }

    public static AccountingProposedEntry BuildProposal(
        IAtomicCommandContext context,
        int portfolioId,
        JournalSourceType sourceType,
        long sourceId,
        string sourceBusinessKey,
        int postingRuleVersion,
        DateOnly effectiveOn,
        string currency,
        string description,
        IEnumerable<AccountingProposedLine> lines,
        int? reversesJournalEntryId = null,
        int? userId = null,
        string? actorLabel = null,
        Guid? authSessionId = null,
        int? accessContextId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lines);

        return new AccountingProposedEntry
        {
            PortfolioId = portfolioId,
            SourceType = sourceType,
            SourceId = sourceId,
            SourceBusinessKey = sourceBusinessKey,
            PostingRuleVersion = postingRuleVersion,
            EffectiveOn = effectiveOn,
            Currency = currency,
            Description = description,
            ReversesJournalEntryId = reversesJournalEntryId,
            AttemptId = context.AttemptId,
            UserId = userId,
            ActorLabel = actorLabel,
            AuthSessionId = authSessionId,
            AccessContextId = accessContextId,
            AtomicReceiptId = context.AtomicReceiptId,
            Lines = AccountingPostingLineOrdering.Order(lines),
        };
    }

    private static async Task<int> RequireSystemAccountIdCoreAsync(
        RentalCommandDbContext db,
        int portfolioId,
        string systemKey,
        CancellationToken ct)
    {
        var accountId = await db.LedgerAccounts
            .Where(account => account.PortfolioId == portfolioId
                && account.SystemKey == systemKey
                && account.IsSystem
                && account.IsActive)
            .Select(account => (int?)account.Id)
            .SingleOrDefaultAsync(ct);
        return accountId ?? throw new AccountingPostingValidationException(
            $"The required accounting mapping '{systemKey}' is missing or inactive.");
    }
}

/// <summary>Applies one fixed source-rule order before account-id ordering.</summary>
public static class AccountingPostingLineOrdering
{
    private static readonly IReadOnlyDictionary<string, int> RuleOrder =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["debit:tenant-receivable"] = 10,
            ["debit:operating-cash"] = 20,
            ["debit:undeposited-funds"] = 30,
            ["debit:security-deposit-trust-cash"] = 40,
            ["debit:expense"] = 50,
            ["debit:asset"] = 60,
            ["debit:accounts-payable"] = 70,
            ["debit:security-deposit-payable"] = 80,
            ["debit:owner-distribution"] = 90,
            ["debit:mortgage-principal"] = 100,
            ["debit:mortgage-interest"] = 110,
            ["debit:mortgage-escrow"] = 120,
            ["debit:cash"] = 130,
            ["credit:tenant-receivable"] = 210,
            ["credit:operating-cash"] = 220,
            ["credit:undeposited-funds"] = 230,
            ["credit:security-deposit-payable"] = 240,
            ["credit:accounts-payable"] = 250,
            ["credit:income"] = 260,
            ["credit:asset"] = 270,
            ["credit:cash"] = 280,
            ["credit:owner-contribution"] = 290,
            ["credit:owner-distribution"] = 300,
        };

    public static IReadOnlyList<AccountingProposedLine> Order(
        IEnumerable<AccountingProposedLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines
            .Select((line, index) => (line, index))
            .OrderBy(item => RuleRank(item.line.SourceLineType))
            .ThenBy(item => item.line.LedgerAccountId)
            .ThenBy(item => item.line.PropertyId ?? 0)
            .ThenBy(item => item.line.UnitId ?? 0)
            .ThenBy(item => item.line.TenantAccountId ?? 0)
            .ThenBy(item => item.line.OwnerEntityId ?? 0)
            .ThenBy(item => item.line.SourceLineId ?? 0)
            .ThenBy(item => item.line.SourceLineType, StringComparer.Ordinal)
            .ThenBy(item => item.line.Memo, StringComparer.Ordinal)
            .ThenBy(item => item.line.DebitAmount)
            .ThenBy(item => item.line.CreditAmount)
            .ThenBy(item => item.index)
            .Select(item => item.line)
            .ToArray();
    }

    private static int RuleRank(string? sourceLineType) =>
        sourceLineType is not null && RuleOrder.TryGetValue(sourceLineType, out var rank)
            ? rank
            : 1000;
}
