using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Accounting;

public sealed class TargetedCreditEligibilityRow
{
    public int PortfolioId { get; init; }
    public int TenantAccountId { get; init; }
    public long TenantLedgerEntryId { get; init; }
    public TenantLedgerEntryType EntryType { get; init; }
    public DateOnly BusinessDate { get; init; }
    public decimal RemainingTargetableAmount { get; init; }
}

/// <summary>
/// Shared DB-translatable authority for the amount of a debit entry that remains available to a
/// targeted credit. Effective charge reversals follow the portfolio business date, while allocation
/// capacity follows the append-only invariant and is consumed as soon as an allocation is written.
/// Callers compose authorization and action-specific eligibility around this projection without
/// materializing ledger rows.
/// </summary>
public static class TargetedCreditEligibilityQuery
{
    public static IQueryable<TargetedCreditEligibilityRow> Build(
        IQueryable<TenantLedgerEntry> targetEntries,
        IQueryable<TenantLedgerEntry> corrections,
        IQueryable<TenantLedgerAllocation> allocations,
        IQueryable<TenantAccountBalanceProjection> accountBalances) =>
        from entry in targetEntries
        join balance in accountBalances
            on new { entry.PortfolioId, entry.TenantAccountId }
            equals new { balance.PortfolioId, balance.TenantAccountId }
        select new TargetedCreditEligibilityRow
        {
            PortfolioId = entry.PortfolioId,
            TenantAccountId = entry.TenantAccountId,
            TenantLedgerEntryId = entry.Id,
            EntryType = entry.EntryType,
            BusinessDate = balance.BusinessDate,
            RemainingTargetableAmount = entry.Amount
                - (corrections
                    .Where(correction => correction.PortfolioId == entry.PortfolioId
                        && correction.TenantAccountId == entry.TenantAccountId
                        && correction.EffectiveOn <= balance.BusinessDate
                        && correction.EntryType == TenantLedgerEntryType.Reversal
                        && correction.ReversesEntryId == entry.Id)
                    .Sum(correction => (decimal?)correction.Amount) ?? 0m)
                - (allocations
                    .Where(allocation => allocation.PortfolioId == entry.PortfolioId
                        && allocation.TenantAccountId == entry.TenantAccountId
                        && allocation.DebitEntryId == entry.Id)
                    .Sum(allocation => (decimal?)allocation.Amount) ?? 0m),
        };
}
