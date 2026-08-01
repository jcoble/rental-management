using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Data.Accounting;

/// <summary>
/// Validates and attaches complete immutable journal entries to the caller's current transaction.
/// The source command owns the transaction and final SaveChanges call.
/// </summary>
public sealed class AccountingPostingService
{
    private const long PostingIdentityLockSeed = 803;
    private readonly RentalCommandDbContext _db;

    public AccountingPostingService(RentalCommandDbContext db) => _db = db;

    /// <summary>
    /// Validates a complete proposed entry, returns an exact committed replay, or attaches a new
    /// entry to the current context. This method deliberately does not open or commit a transaction.
    /// </summary>
    public async Task<JournalEntry> PostAsync(
        AccountingProposedEntry proposal,
        CancellationToken ct = default)
    {
        var prepared = Prepare(proposal);
        await AcquireBusinessIdentityLockAsync(prepared.Identity, ct);

        var existing = await FindExistingAsync(prepared.Identity, ct);
        if (existing is not null)
            return ReplayOrConflict(existing, prepared.Digest);

        await ValidateAccountAndCurrencyFactsAsync([prepared], ct);
        await ValidateExactReversalAsync(prepared, ct);
        return Attach(prepared);
    }

    /// <summary>
    /// Posts a homogeneous source batch without per-entry account or idempotency reads. Callers
    /// use this only for bounded, set-based source batches that already own one atomic transaction.
    /// </summary>
    public async Task<IReadOnlyList<JournalEntry>> PostBatchAsync(
        IReadOnlyCollection<AccountingProposedEntry> proposals,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        if (proposals.Count == 0)
            return Array.Empty<JournalEntry>();

        var prepared = proposals.Select(Prepare).ToArray();
        var identities = new HashSet<PostingIdentity>();
        foreach (var item in prepared)
        {
            if (!identities.Add(item.Identity))
            {
                throw new AccountingPostingValidationException(
                    "A posting batch cannot contain the same source business identity twice.");
            }
        }

        var existingByIdentity = await FindExistingBatchAsync(prepared, ct);
        var results = new JournalEntry[prepared.Length];
        var newPostings = new List<(int Index, PreparedPosting Posting)>();
        for (var index = 0; index < prepared.Length; index++)
        {
            var item = prepared[index];
            if (existingByIdentity.TryGetValue(item.Identity, out var existing))
            {
                results[index] = ReplayOrConflict(existing, item.Digest);
                continue;
            }

            newPostings.Add((index, item));
        }

        if (newPostings.Count == 0)
            return results;

        await ValidateAccountAndCurrencyFactsAsync(
            newPostings.Select(item => item.Posting).ToArray(),
            ct);

        foreach (var (_, item) in newPostings)
        {
            await ValidateExactReversalAsync(item, ct);
        }

        foreach (var (index, item) in newPostings)
        {
            results[index] = Attach(item);
        }

        return results;
    }

    private PreparedPosting Prepare(AccountingProposedEntry proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        proposal.Lines = AccountingPostingLineOrdering.Order(proposal.Lines);
        ValidateProposalShape(proposal);
        return new PreparedPosting(
            proposal,
            new PostingIdentity(
                proposal.PortfolioId,
                proposal.SourceType,
                proposal.SourceBusinessKey.Trim(),
                proposal.PostingRuleVersion),
            ComputeIdempotencyDigest(proposal));
    }

    private async Task AcquireBusinessIdentityLockAsync(
        PostingIdentity identity,
        CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql())
            return;

        var resource = $"{identity.PortfolioId}:{identity.SourceType}:{identity.SourceBusinessKey}:{identity.PostingRuleVersion}";
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({resource}, {PostingIdentityLockSeed}))",
            ct);
    }

    private Task<JournalEntry?> FindExistingAsync(
        PostingIdentity identity,
        CancellationToken ct) =>
        _db.JournalEntries.SingleOrDefaultAsync(entry =>
            entry.PortfolioId == identity.PortfolioId &&
            entry.SourceType == identity.SourceType &&
            entry.SourceBusinessKey == identity.SourceBusinessKey &&
            entry.PostingRuleVersion == identity.PostingRuleVersion,
            ct);

    private async Task<Dictionary<PostingIdentity, JournalEntry>> FindExistingBatchAsync(
        IReadOnlyCollection<PreparedPosting> proposals,
        CancellationToken ct)
    {
        var portfolioIds = proposals.Select(item => item.Identity.PortfolioId).Distinct().ToArray();
        var sourceTypes = proposals.Select(item => item.Identity.SourceType).Distinct().ToArray();
        var businessKeys = proposals.Select(item => item.Identity.SourceBusinessKey).Distinct().ToArray();
        var ruleVersions = proposals.Select(item => item.Identity.PostingRuleVersion).Distinct().ToArray();

        var candidates = await _db.JournalEntries
            .Where(entry => portfolioIds.Contains(entry.PortfolioId)
                && sourceTypes.Contains(entry.SourceType)
                && businessKeys.Contains(entry.SourceBusinessKey)
                && ruleVersions.Contains(entry.PostingRuleVersion))
            .ToListAsync(ct);

        return candidates.ToDictionary(
            entry => new PostingIdentity(
                entry.PortfolioId,
                entry.SourceType,
                entry.SourceBusinessKey,
                entry.PostingRuleVersion));
    }

    private static JournalEntry ReplayOrConflict(JournalEntry existing, string digest)
    {
        if (!string.Equals(existing.IdempotencyDigest, digest, StringComparison.Ordinal))
        {
            throw new AccountingIdempotencyConflictException(
                "The source posting key was already used with different accounting facts.");
        }

        return existing;
    }

    private async Task ValidateAccountAndCurrencyFactsAsync(
        IReadOnlyCollection<PreparedPosting> postings,
        CancellationToken ct)
    {
        var portfolioIds = postings.Select(item => item.Proposal.PortfolioId).Distinct().ToArray();
        var accountIds = postings.SelectMany(item => item.Proposal.Lines)
            .Select(line => line.LedgerAccountId)
            .Distinct()
            .ToArray();

        var facts = await (
                from account in _db.LedgerAccounts.AsNoTracking()
                join portfolio in _db.Portfolios.AsNoTracking()
                    on account.PortfolioId equals portfolio.Id
                where portfolioIds.Contains(account.PortfolioId)
                    && accountIds.Contains(account.Id)
                select new AccountPostingFact(
                    account.Id,
                    account.PortfolioId,
                    account.IsActive,
                    portfolio.Currency))
            .ToListAsync(ct);
        var factsByAccount = facts.ToDictionary(fact => new AccountPortfolioKey(
            fact.AccountId,
            fact.PortfolioId));

        foreach (var posting in postings)
        {
            foreach (var line in posting.Proposal.Lines)
            {
                if (!factsByAccount.TryGetValue(
                        new AccountPortfolioKey(line.LedgerAccountId, posting.Proposal.PortfolioId),
                        out var fact) || !fact.IsActive)
                {
                    // Do not distinguish a missing account from an account in another portfolio.
                    throw new AccountingPostingValidationException(
                        "One or more ledger accounts are unavailable.");
                }

                if (!string.Equals(fact.PortfolioCurrency, posting.Proposal.Currency, StringComparison.Ordinal))
                {
                    throw new AccountingPostingValidationException(
                        "The journal currency must match the portfolio currency.");
                }
            }
        }
    }

    private async Task ValidateExactReversalAsync(PreparedPosting posting, CancellationToken ct)
    {
        if (posting.Proposal.ReversesJournalEntryId is not { } reversalId)
            return;

        var linesJson = JsonSerializer.Serialize(posting.Proposal.Lines.Select(line => new ReversalLineInput(
            line.LedgerAccountId,
            line.DebitAmount,
            line.CreditAmount,
            TrimOrNull(line.Memo),
            line.PropertyId,
            line.UnitId,
            line.TenantAccountId,
            line.OwnerEntityId,
            TrimOrNull(line.SourceLineType),
            line.SourceLineId)));
        var valid = await _db.Database.SqlQuery<int>($"""
            WITH proposed AS (
                SELECT *
                FROM jsonb_to_recordset(CAST({linesJson} AS jsonb)) AS line(
                    "LedgerAccountId" integer,
                    "DebitAmount" numeric,
                    "CreditAmount" numeric,
                    "Memo" text,
                    "PropertyId" integer,
                    "UnitId" integer,
                    "TenantAccountId" integer,
                    "OwnerEntityId" integer,
                    "SourceLineType" text,
                    "SourceLineId" bigint)
            )
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM "JournalEntries" original
                WHERE original."Id" = {reversalId}
                  AND original."PortfolioId" = {posting.Proposal.PortfolioId}
                  AND original."Currency" = {posting.Proposal.Currency}
                  AND NOT EXISTS (
                      (SELECT line."LedgerAccountId", line."DebitAmount", line."CreditAmount",
                              line."Memo", line."PropertyId", line."UnitId", line."TenantAccountId",
                              line."OwnerEntityId", line."SourceLineType", line."SourceLineId"
                       FROM "JournalLines" line
                       WHERE line."JournalEntryId" = original."Id")
                      EXCEPT ALL
                      (SELECT line."LedgerAccountId", line."CreditAmount", line."DebitAmount",
                              line."Memo", line."PropertyId", line."UnitId", line."TenantAccountId",
                              line."OwnerEntityId", line."SourceLineType", line."SourceLineId"
                       FROM proposed line)
                  )
                  AND NOT EXISTS (
                      (SELECT line."LedgerAccountId", line."CreditAmount", line."DebitAmount",
                              line."Memo", line."PropertyId", line."UnitId", line."TenantAccountId",
                              line."OwnerEntityId", line."SourceLineType", line."SourceLineId"
                       FROM "JournalLines" line
                       WHERE line."JournalEntryId" = original."Id")
                      EXCEPT ALL
                      (SELECT line."LedgerAccountId", line."DebitAmount", line."CreditAmount",
                              line."Memo", line."PropertyId", line."UnitId", line."TenantAccountId",
                              line."OwnerEntityId", line."SourceLineType", line."SourceLineId"
                       FROM proposed line)
                  )
            ) THEN 1 ELSE 0 END AS "Value"
            """).SingleAsync(ct);

        if (valid != 1)
        {
            throw new AccountingPostingValidationException(
                "A reversal must exactly mirror the original journal entry.");
        }
    }

    private JournalEntry Attach(PreparedPosting posting)
    {
        var proposal = posting.Proposal;
        var entry = new JournalEntry
        {
            PortfolioId = proposal.PortfolioId,
            EffectiveOn = proposal.EffectiveOn,
            PostedAtUtc = DateTime.UtcNow,
            Currency = proposal.Currency,
            Description = proposal.Description.Trim(),
            SourceType = proposal.SourceType,
            SourceId = proposal.SourceId,
            SourceBusinessKey = proposal.SourceBusinessKey.Trim(),
            IdempotencyDigest = posting.Digest,
            PostingRuleVersion = proposal.PostingRuleVersion,
            ReversesJournalEntryId = proposal.ReversesJournalEntryId,
            AttemptId = proposal.AttemptId,
            UserId = proposal.UserId,
            ActorLabel = string.IsNullOrWhiteSpace(proposal.ActorLabel) ? null : proposal.ActorLabel.Trim(),
            AuthSessionId = proposal.AuthSessionId,
            AccessContextId = proposal.AccessContextId,
            AtomicReceiptId = proposal.AtomicReceiptId,
        };

        foreach (var proposedLine in proposal.Lines)
        {
            entry.Lines.Add(new JournalLine
            {
                LedgerAccountId = proposedLine.LedgerAccountId,
                DebitAmount = proposedLine.DebitAmount,
                CreditAmount = proposedLine.CreditAmount,
                Memo = TrimOrNull(proposedLine.Memo),
                PropertyId = proposedLine.PropertyId,
                UnitId = proposedLine.UnitId,
                TenantAccountId = proposedLine.TenantAccountId,
                OwnerEntityId = proposedLine.OwnerEntityId,
                SourceLineType = TrimOrNull(proposedLine.SourceLineType),
                SourceLineId = proposedLine.SourceLineId,
            });
        }

        _db.JournalEntries.Add(entry);
        return entry;
    }

    private static void ValidateProposalShape(AccountingProposedEntry proposal)
    {
        if (proposal.PortfolioId <= 0 || proposal.SourceId <= 0 || proposal.PostingRuleVersion <= 0)
            throw new AccountingPostingValidationException("A portfolio, source, and posting-rule version are required.");
        if (string.IsNullOrWhiteSpace(proposal.SourceBusinessKey) || proposal.SourceBusinessKey.Trim().Length > 200)
            throw new AccountingPostingValidationException("A source business key is required and must be 200 characters or fewer.");
        if (string.IsNullOrWhiteSpace(proposal.Description) || proposal.Description.Trim().Length > 1000)
            throw new AccountingPostingValidationException("A journal description is required and must be 1000 characters or fewer.");
        if (string.IsNullOrWhiteSpace(proposal.Currency) || proposal.Currency.Length != 3 ||
            proposal.Currency.Any(character => character is < 'A' or > 'Z'))
            throw new AccountingPostingValidationException("Currency must be a three-letter uppercase code.");
        if (proposal.AttemptId == Guid.Empty || proposal.AtomicReceiptId == Guid.Empty)
            throw new AccountingPostingValidationException("The atomic attempt and receipt identities are required.");
        if ((!proposal.UserId.HasValue || proposal.UserId.Value <= 0) && string.IsNullOrWhiteSpace(proposal.ActorLabel))
            throw new AccountingPostingValidationException("An actor user or explicit actor label is required.");
        if (proposal.Lines is null || proposal.Lines.Count == 0)
            throw new AccountingPostingValidationException("At least one journal line is required.");

        foreach (var line in proposal.Lines)
        {
            if (line.LedgerAccountId <= 0)
                throw new AccountingPostingValidationException("Every journal line requires an account.");
            if (line.DebitAmount < 0 || line.CreditAmount < 0)
                throw new AccountingPostingValidationException("Journal amounts cannot be negative.");
            if (decimal.Round(line.DebitAmount, 2, MidpointRounding.ToEven) != line.DebitAmount ||
                decimal.Round(line.CreditAmount, 2, MidpointRounding.ToEven) != line.CreditAmount)
            {
                throw new AccountingPostingValidationException(
                    "Journal amounts cannot include fractions of a cent.");
            }
            if ((line.DebitAmount > 0) == (line.CreditAmount > 0))
                throw new AccountingPostingValidationException("Each line must contain exactly one positive debit or credit.");
            if (line.Memo?.Length > 1000 || line.SourceLineType?.Length > 80)
                throw new AccountingPostingValidationException("Journal line text is too long.");
        }

        var debitTotal = proposal.Lines.Sum(line => line.DebitAmount);
        var creditTotal = proposal.Lines.Sum(line => line.CreditAmount);
        if (debitTotal != creditTotal)
        {
            throw new AccountingPostingValidationException(
                $"The journal entry is not balanced for currency {proposal.Currency}.");
        }
    }

    internal static string ComputeIdempotencyDigest(AccountingProposedEntry proposal)
    {
        var canonical = new StringBuilder()
            .Append(Value(proposal.PortfolioId))
            .Append(Value(proposal.SourceType))
            .Append(Value(proposal.SourceBusinessKey.Trim()))
            .Append(Value(proposal.PostingRuleVersion))
            .Append(Value(proposal.EffectiveOn))
            .Append(Value(proposal.Currency))
            .Append(Value(proposal.Description.Trim()))
            .Append(Value(proposal.ReversesJournalEntryId))
            .ToString();

        foreach (var line in proposal.Lines)
        {
            canonical += Value(line.LedgerAccountId)
                + Value(line.DebitAmount)
                + Value(line.CreditAmount)
                + Value(TrimOrNull(line.Memo))
                + Value(line.PropertyId)
                + Value(line.UnitId)
                + Value(line.TenantAccountId)
                + Value(line.OwnerEntityId)
                + Value(TrimOrNull(line.SourceLineType));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Value<T>(T? value) =>
        value switch
        {
            null => "0:",
            DateOnly date => LengthPrefix(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            decimal amount => LengthPrefix(amount.ToString("0.#############################", CultureInfo.InvariantCulture)),
            _ => LengthPrefix(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };

    private static string LengthPrefix(string value) => $"{value.Length}:{value}|";

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PreparedPosting(
        AccountingProposedEntry Proposal,
        PostingIdentity Identity,
        string Digest);

    private sealed record AccountPostingFact(
        int AccountId,
        int PortfolioId,
        bool IsActive,
        string PortfolioCurrency);

    private sealed record ReversalLineInput(
        int LedgerAccountId,
        decimal DebitAmount,
        decimal CreditAmount,
        string? Memo,
        int? PropertyId,
        int? UnitId,
        int? TenantAccountId,
        int? OwnerEntityId,
        string? SourceLineType,
        long? SourceLineId);

    private readonly record struct PostingIdentity(
        int PortfolioId,
        JournalSourceType SourceType,
        string SourceBusinessKey,
        int PostingRuleVersion);

    private readonly record struct AccountPortfolioKey(int AccountId, int PortfolioId);
}
