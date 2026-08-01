using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Data.Accounting;

/// <summary>
/// Validates and attaches one complete immutable journal entry to the caller's current context.
/// The source command owns the transaction and SaveChanges call.
/// </summary>
public sealed class AccountingPostingService
{
    private readonly RentalCommandDbContext _db;

    public AccountingPostingService(RentalCommandDbContext db) => _db = db;

    /// <summary>
    /// Validates a complete proposed entry, returns an exact committed replay, or attaches a new
    /// entry to the current context. This method deliberately does not save or open a transaction.
    /// </summary>
    public async Task<JournalEntry> PostAsync(
        AccountingProposedEntry proposal,
        CancellationToken ct = default)
    {
        ValidateProposalShape(proposal);
        var digest = ComputeIdempotencyDigest(proposal);

        var existing = await _db.JournalEntries.SingleOrDefaultAsync(entry =>
            entry.PortfolioId == proposal.PortfolioId &&
            entry.SourceType == proposal.SourceType &&
            entry.SourceId == proposal.SourceId &&
            entry.PostingRuleVersion == proposal.PostingRuleVersion, ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.IdempotencyDigest, digest, StringComparison.Ordinal))
                throw new AccountingIdempotencyConflictException(
                    "The source posting key was already used with different accounting facts.");

            return existing;
        }

        var accountIds = proposal.Lines.Select(line => line.LedgerAccountId).Distinct().ToArray();
        var accounts = await _db.LedgerAccounts
            .Where(account => account.PortfolioId == proposal.PortfolioId && accountIds.Contains(account.Id))
            .Select(account => new { account.Id, account.IsActive })
            .ToListAsync(ct);

        // Use the same message for missing and inactive rows. A caller must not be able to infer
        // whether an account id exists in a different portfolio.
        if (accounts.Count != accountIds.Length || accounts.Any(account => !account.IsActive))
            throw new AccountingPostingValidationException("One or more ledger accounts are unavailable.");

        var debitTotal = proposal.Lines.Sum(line => line.DebitAmount);
        var creditTotal = proposal.Lines.Sum(line => line.CreditAmount);
        if (debitTotal != creditTotal)
            throw new AccountingPostingValidationException(
                $"The journal entry is not balanced for currency {proposal.Currency}.");

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
            IdempotencyDigest = digest,
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
        if (proposal is null)
            throw new ArgumentNullException(nameof(proposal));
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
            if ((line.DebitAmount > 0) == (line.CreditAmount > 0))
                throw new AccountingPostingValidationException("Each line must contain exactly one positive debit or credit.");
            if (line.Memo?.Length > 1000 || line.SourceLineType?.Length > 80)
                throw new AccountingPostingValidationException("Journal line text is too long.");
        }
    }

    internal static string ComputeIdempotencyDigest(AccountingProposedEntry proposal)
    {
        var canonical = new StringBuilder()
            .Append(Value(proposal.PortfolioId))
            .Append(Value(proposal.SourceType))
            .Append(Value(proposal.SourceId))
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
                + Value(line.Memo)
                + Value(line.PropertyId)
                + Value(line.UnitId)
                + Value(line.TenantAccountId)
                + Value(line.OwnerEntityId)
                + Value(line.SourceLineType)
                + Value(line.SourceLineId);
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
}
