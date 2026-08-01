using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Data.Accounting;

/// <summary>Deterministic conversion framework seam; source-specific generators are added later.</summary>
public interface IAccountingSourceJournalGenerator
{
    JournalSourceType SourceType { get; }

    /// <summary>
    /// Returns one bounded, ordered page of immutable source identities. The generator must keep
    /// filtering and paging in the database; the conversion service never loads a portfolio first.
    /// </summary>
    Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default);

    Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default);

    /// <summary>Computes the source amount in SQL for the reconciliation row.</summary>
    Task<decimal> GetSourceTotalAsync(int portfolioId, string currency, CancellationToken ct = default);
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
        SourceTypes = _generators.Keys.OrderBy(sourceType => sourceType).ToArray();
    }

    public IReadOnlyList<JournalSourceType> SourceTypes { get; }

    public async Task<AccountingProposedEntry?> GenerateAsync(
        AccountingSourceJournalKey key,
        CancellationToken ct = default)
    {
        if (!_generators.TryGetValue(key.SourceType, out var generator))
            return null;
        return await generator.GenerateAsync(key, ct);
    }

    public Task<IReadOnlyList<long>> GetSourceIdsAsync(
        int portfolioId,
        JournalSourceType sourceType,
        long afterSourceId,
        int batchSize,
        CancellationToken ct = default) =>
        _generators.TryGetValue(sourceType, out var generator)
            ? generator.GetSourceIdsAsync(portfolioId, afterSourceId, batchSize, ct)
            : Task.FromResult<IReadOnlyList<long>>([]);

    public Task<decimal> GetSourceTotalAsync(
        int portfolioId,
        JournalSourceType sourceType,
        string currency,
        CancellationToken ct = default) =>
        _generators.TryGetValue(sourceType, out var generator)
            ? generator.GetSourceTotalAsync(portfolioId, currency, ct)
            : Task.FromResult(0m);
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
