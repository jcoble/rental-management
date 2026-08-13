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
/// targeted credit at the portfolio business date. Callers compose authorization and action-specific
/// eligibility around this projection without materializing ledger rows.
/// </summary>
public static class TargetedCreditEligibilityQuery
{
    public static IQueryable<TargetedCreditEligibilityRow> Build(
        IQueryable<TenantLedgerEntry> targetEntries,
        IQueryable<TenantLedgerEntry> corrections,
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
                        && ((correction.EntryType == TenantLedgerEntryType.Reversal
                                && correction.ReversesEntryId == entry.Id)
                            || (correction.EntryType == TenantLedgerEntryType.Credit
                                && correction.RelatedTenantLedgerEntryId == entry.Id)))
                    .Sum(correction => (decimal?)correction.Amount) ?? 0m),
        };
}
