using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Data.Accounting;

/// <summary>Deterministic conversion framework seam; source-specific generators are added later.</summary>
public interface IAccountingSourceJournalGenerator
{
    JournalSourceType SourceType { get; }

    Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default);
}

/// <summary>
/// Coordinates source generators without loading historical portfolios. No source generator is
/// registered by the foundation lane, so a conversion run remains explicit and reviewable.
/// </summary>
public sealed class AccountingConversionFramework
{
    private readonly IReadOnlyDictionary<JournalSourceType, IAccountingSourceJournalGenerator> _generators;

    public AccountingConversionFramework(IEnumerable<IAccountingSourceJournalGenerator> generators)
    {
        _generators = generators
            .ToDictionary(generator => generator.SourceType);
    }

    public async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        if (!_generators.TryGetValue(key.SourceType, out var generator))
            return null;
        return await generator.GenerateAsync(key, ct);
    }
}

/// <summary>Typed reconciliation row helper that keeps the conversion key server-queryable.</summary>
public sealed class AccountingConversionReconciliationService
{
    private readonly RentalCommandDbContext _db;

    public AccountingConversionReconciliationService(RentalCommandDbContext db) => _db = db;

    public Task<AccountingConversionReconciliation?> FindAsync(
        int portfolioId,
        JournalSourceType sourceType,
        string currency,
        int postingRuleVersion,
        CancellationToken ct = default) =>
        _db.AccountingConversionReconciliations.SingleOrDefaultAsync(row =>
            row.PortfolioId == portfolioId &&
            row.SourceType == sourceType &&
            row.Currency == currency &&
            row.PostingRuleVersion == postingRuleVersion, ct);
}
