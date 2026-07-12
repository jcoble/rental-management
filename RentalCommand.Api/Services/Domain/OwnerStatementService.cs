using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOwnerStatementService"/>
public class OwnerStatementService : IOwnerStatementService
{
    private readonly RentalCommandDbContext _db;

    public OwnerStatementService(RentalCommandDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<OwnerStatementReport?> GetForOwnerAsync(
        int portfolioId, int ownerId, int year, CancellationToken ct = default)
    {
        // ── Verify the owner exists in the portfolio ────────────────────────────────────────────
        var owner = await _db.OwnerEntities
            .AsNoTracking()
            .Where(o => o.PortfolioId == portfolioId && o.Id == ownerId)
            .Select(o => new { o.Id, o.Name })
            .FirstOrDefaultAsync(ct);

        if (owner is null)
            return null;

        var (start, end) = YearRange(year);
        var totalDistributed = await _db.OwnerDistributions
            .AsNoTracking()
            .Where(d =>
                d.PortfolioId == portfolioId &&
                d.OwnerEntityId == ownerId &&
                d.Date >= start &&
                d.Date < end)
            .SumAsync(d => (decimal?)d.Amount, ct) ?? 0m;

        // ── Property lines ─────────────────────────────────────────────────────────────────────
        // Filter/sort each owned property and project its receipt-allocation/expense aggregates in SQL.
        // A second SQL aggregate produces totals from rounded property rows so application code only
        // formats the bounded property breakdown.
        var ownerPropertyRows = OwnerPropertyNetRows(portfolioId, year)
            .Where(row => row.OwnerId == ownerId);
        var propertyRows = await ownerPropertyRows
            .OrderBy(row => row.PropertyName)
            .ToListAsync(ct);

        var statementTotals = await ownerPropertyRows
            .Select(row => new
            {
                RentalIncome = Math.Round(row.RentalIncome, 2),
                Expenses = Math.Round(row.Expenses, 2),
                ManagementFee = Math.Round(row.RentalIncome * row.ManagementFeePercent / 100m, 2),
            })
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalIncome = group.Sum(row => row.RentalIncome),
                TotalExpenses = group.Sum(row => row.Expenses),
                TotalManagementFee = group.Sum(row => row.ManagementFee),
                TotalNetToOwner = group.Sum(row =>
                    row.RentalIncome - row.Expenses - row.ManagementFee),
            })
            .SingleOrDefaultAsync(ct);

        if (propertyRows.Count == 0)
        {
            return new OwnerStatementReport
            {
                OwnerId = owner.Id,
                OwnerName = owner.Name,
                Year = year,
                Properties = [],
                TotalIncome = 0m,
                TotalExpenses = 0m,
                TotalManagementFee = 0m,
                TotalNetToOwner = 0m,
                TotalDistributed = totalDistributed,
                Undistributed = -totalDistributed,
            };
        }

        // ── Assemble per-property lines ──────────────────────────────────────────────────────────
        var lines = new List<OwnerStatementPropertyLine>(propertyRows.Count);

        foreach (var prop in propertyRows)
        {
            var income = Math.Round(prop.RentalIncome, 2);
            var expenses = Math.Round(prop.Expenses, 2);
            var mgmtFee = Math.Round(income * prop.ManagementFeePercent / 100m, 2);
            var net = income - expenses - mgmtFee;

            lines.Add(new OwnerStatementPropertyLine(
                PropertyId: prop.PropertyId,
                PropertyName: prop.PropertyName,
                RentalIncome: income,
                Expenses: expenses,
                ManagementFee: mgmtFee,
                NetToOwner: net));
        }

        // PostgreSQL sums the rounded per-property values, so the printed property lines and totals foot
        // exactly without moving any aggregation or owner-distribution join into application memory.
        var totalNetToOwner = statementTotals?.TotalNetToOwner ?? 0m;

        return new OwnerStatementReport
        {
            OwnerId = owner.Id,
            OwnerName = owner.Name,
            Year = year,
            Properties = lines,
            TotalIncome = statementTotals?.TotalIncome ?? 0m,
            TotalExpenses = statementTotals?.TotalExpenses ?? 0m,
            TotalManagementFee = statementTotals?.TotalManagementFee ?? 0m,
            TotalNetToOwner = totalNetToOwner,
            TotalDistributed = totalDistributed,
            Undistributed = totalNetToOwner - totalDistributed,
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OwnerStatementSummary>> ListOwnersWithNetAsync(
        int portfolioId, int year, CancellationToken ct = default)
    {
        var propertyNetRows = OwnerPropertyNetRows(portfolioId, year);
        var (start, end) = YearRange(year);

        var summaries = await propertyNetRows
            .GroupBy(p => new { p.OwnerId, p.OwnerName })
            .Select(g => new
            {
                g.Key.OwnerId,
                g.Key.OwnerName,
                NetToOwner = g.Sum(p =>
                    p.RentalIncome -
                    p.Expenses -
                    (p.RentalIncome * p.ManagementFeePercent / 100m)),
                TotalDistributed = _db.OwnerDistributions
                    .Where(distribution =>
                        distribution.PortfolioId == portfolioId &&
                        distribution.OwnerEntityId == g.Key.OwnerId &&
                        distribution.Date >= start &&
                        distribution.Date < end)
                    .Sum(distribution => (decimal?)distribution.Amount) ?? 0m,
            })
            .OrderBy(o => o.OwnerName)
            .ToListAsync(ct);

        return summaries
            .Select(s => new OwnerStatementSummary(
                s.OwnerId,
                s.OwnerName,
                s.NetToOwner,
                s.TotalDistributed,
                s.NetToOwner - s.TotalDistributed))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<decimal> GetTotalNetToOwnersAsync(int portfolioId, int year, CancellationToken ct = default)
    {
        return await OwnerPropertyNetRows(portfolioId, year)
            .GroupBy(_ => 1)
            .Select(g => g.Sum(p =>
                p.RentalIncome -
                p.Expenses -
                (p.RentalIncome * p.ManagementFeePercent / 100m)))
            .SingleOrDefaultAsync(ct);
    }

    private IQueryable<OwnerPropertyNetRow> OwnerPropertyNetRows(int portfolioId, int year)
    {
        var (startOn, endOn) = YearDateRange(year);

        return _db.Properties
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.OwnerEntityId != null &&
                p.OwnerEntity != null)
            .Select(p => new OwnerPropertyNetRow
            {
                PropertyId = p.Id,
                PropertyName = p.Name,
                OwnerId = p.OwnerEntityId!.Value,
                OwnerName = p.OwnerEntity!.Name,
                ManagementFeePercent = p.ManagementFeePercent ?? 0m,
                RentalIncome = (
                    from allocation in _db.TenantLedgerAllocations
                    join credit in _db.TenantLedgerEntries
                        on new { allocation.PortfolioId, Id = allocation.CreditEntryId }
                        equals new { credit.PortfolioId, credit.Id }
                    join debit in _db.TenantLedgerEntries
                        on new { allocation.PortfolioId, Id = allocation.DebitEntryId }
                        equals new { debit.PortfolioId, debit.Id }
                    join account in _db.TenantAccounts
                        on new { allocation.PortfolioId, Id = allocation.TenantAccountId }
                        equals new { account.PortfolioId, account.Id }
                    join management in _db.LeaseManagements
                        on new { account.PortfolioId, Id = account.LeaseManagementId }
                        equals new { management.PortfolioId, management.Id }
                    where allocation.PortfolioId == portfolioId
                        && management.PropertyId == p.Id
                        && credit.EntryType == TenantLedgerEntryType.PaymentReceipt
                        && credit.EffectiveOn >= startOn
                        && credit.EffectiveOn < endOn
                        && debit.EntryType == TenantLedgerEntryType.RentCharge
                    select (decimal?)allocation.Amount).Sum() ?? 0m,
                Expenses = _db.Expenses
                    .Where(e =>
                        e.PortfolioId == portfolioId &&
                        e.PropertyId == p.Id &&
                        e.Status == ExpenseStatus.Paid &&
                        // Sargable half-open year range (was .Year ==).
                        (e.PaidAt ?? e.IncurredAt) >= new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) &&
                        (e.PaidAt ?? e.IncurredAt) < new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
            });
    }

    private static (DateTime Start, DateTime End) YearRange(int year)
    {
        return (
            new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private static (DateOnly Start, DateOnly End) YearDateRange(int year)
    {
        return (new DateOnly(year, 1, 1), new DateOnly(year + 1, 1, 1));
    }

    private sealed class OwnerPropertyNetRow
    {
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public int OwnerId { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public decimal ManagementFeePercent { get; set; }
        public decimal RentalIncome { get; set; }
        public decimal Expenses { get; set; }
    }
}
