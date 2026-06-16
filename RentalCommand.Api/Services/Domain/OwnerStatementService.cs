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

        // ── Load this owner's properties ────────────────────────────────────────────────────────
        var properties = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.OwnerEntityId == ownerId)
            .Select(p => new { p.Id, p.Name, p.ManagementFeePercent })
            .ToListAsync(ct);

        if (properties.Count == 0)
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
            };
        }

        var propertyIds = properties.Select(p => p.Id).ToHashSet();

        // ── Rental income: Paid Rent payments in year, keyed by PropertyId via Lease ───────────
        // Grouped + summed SQL-side (one row per property), not by grouping materialized rows.
        var incomeByProperty = (await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.PaymentType == PaymentType.Rent &&
                p.Status == PaymentStatus.Paid &&
                p.PaidDate != null &&
                p.PaidDate.Value.Year == year &&
                p.Lease != null &&
                propertyIds.Contains(p.Lease!.PropertyId))
            .GroupBy(p => p.Lease!.PropertyId)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(p => p.Amount) })
            .ToListAsync(ct))
            .ToDictionary(g => g.PropertyId, g => g.Total);

        // ── Expenses in year, keyed by PropertyId ────────────────────────────────────────────────
        // Cash basis (to match the cash-basis rental income above): only Paid expenses count, dated by
        // PaidAt (falling back to IncurredAt) — the same COALESCE(PaidAt, IncurredAt) convention used
        // across the accounting/reports services and the vw_accounting_transactions view.
        var expensesByProperty = (await _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.PropertyId != null &&
                propertyIds.Contains(e.PropertyId!.Value) &&
                e.Status == ExpenseStatus.Paid &&
                (e.PaidAt ?? e.IncurredAt).Year == year)
            .GroupBy(e => e.PropertyId!.Value)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(e => e.Amount) })
            .ToListAsync(ct))
            .ToDictionary(g => g.PropertyId, g => g.Total);

        // ── Assemble per-property lines ──────────────────────────────────────────────────────────
        var lines = new List<OwnerStatementPropertyLine>(properties.Count);

        foreach (var prop in properties)
        {
            var income   = Math.Round(incomeByProperty.GetValueOrDefault(prop.Id, 0m), 2);
            var expenses = Math.Round(expensesByProperty.GetValueOrDefault(prop.Id, 0m), 2);
            var mgmtFee  = Math.Round(income * (prop.ManagementFeePercent ?? 0m) / 100m, 2);
            var net      = income - expenses - mgmtFee;

            lines.Add(new OwnerStatementPropertyLine(
                PropertyId:    prop.Id,
                PropertyName:  prop.Name,
                RentalIncome:  income,
                Expenses:      expenses,
                ManagementFee: mgmtFee,
                NetToOwner:    net));
        }

        lines.Sort((a, b) => string.Compare(a.PropertyName, b.PropertyName, StringComparison.OrdinalIgnoreCase));

        var totalIncome    = lines.Sum(l => l.RentalIncome);
        var totalExpenses  = lines.Sum(l => l.Expenses);
        var totalMgmtFee   = lines.Sum(l => l.ManagementFee);
        var totalNet       = lines.Sum(l => l.NetToOwner);

        return new OwnerStatementReport
        {
            OwnerId            = owner.Id,
            OwnerName          = owner.Name,
            Year               = year,
            Properties         = lines,
            TotalIncome        = totalIncome,
            TotalExpenses      = totalExpenses,
            TotalManagementFee = totalMgmtFee,
            TotalNetToOwner    = totalNet,
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OwnerStatementSummary>> ListOwnersWithNetAsync(
        int portfolioId, int year, CancellationToken ct = default)
    {
        // Load all OwnerEntities that have at least one property in this portfolio.
        var owners = await _db.OwnerEntities
            .AsNoTracking()
            .Where(o => o.PortfolioId == portfolioId &&
                        _db.Properties.Any(p => p.PortfolioId == portfolioId && p.OwnerEntityId == o.Id))
            .Select(o => new { o.Id, o.Name })
            .OrderBy(o => o.Name)
            .ToListAsync(ct);

        if (owners.Count == 0)
            return [];

        // Load properties for all these owners in one shot.
        var ownerIds = owners.Select(o => o.Id).ToHashSet();

        var properties = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                        p.OwnerEntityId != null &&
                        ownerIds.Contains(p.OwnerEntityId!.Value))
            .Select(p => new { p.Id, OwnerId = p.OwnerEntityId, p.ManagementFeePercent })
            .ToListAsync(ct);

        var propertyIds = properties.Select(p => p.Id).ToHashSet();

        // Load income and expenses for those properties — grouped + summed SQL-side (one row per
        // property each), not by grouping the materialized payment/expense rows in memory.
        var incomeByProperty = (await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.PaymentType == PaymentType.Rent &&
                p.Status == PaymentStatus.Paid &&
                p.PaidDate != null &&
                p.PaidDate.Value.Year == year &&
                p.Lease != null &&
                propertyIds.Contains(p.Lease!.PropertyId))
            .GroupBy(p => p.Lease!.PropertyId)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(p => p.Amount) })
            .ToListAsync(ct))
            .ToDictionary(g => g.PropertyId, g => g.Total);

        // Cash basis (to match the cash-basis rental income above): only Paid expenses count, dated by
        // PaidAt (falling back to IncurredAt) — the same COALESCE(PaidAt, IncurredAt) convention used
        // across the accounting/reports services and the vw_accounting_transactions view.
        var expensesByProperty = (await _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.PropertyId != null &&
                propertyIds.Contains(e.PropertyId!.Value) &&
                e.Status == ExpenseStatus.Paid &&
                (e.PaidAt ?? e.IncurredAt).Year == year)
            .GroupBy(e => e.PropertyId!.Value)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(e => e.Amount) })
            .ToListAsync(ct))
            .ToDictionary(g => g.PropertyId, g => g.Total);

        // Group properties by owner and compute net for each.
        var propsByOwner = properties.GroupBy(p => p.OwnerId!.Value)
                                     .ToDictionary(g => g.Key, g => g.ToList());

        var summaries = new List<OwnerStatementSummary>(owners.Count);

        foreach (var o in owners)
        {
            var ownerProps = propsByOwner.GetValueOrDefault(o.Id) ?? [];
            var net = 0m;

            foreach (var prop in ownerProps)
            {
                var income   = Math.Round(incomeByProperty.GetValueOrDefault(prop.Id, 0m), 2);
                var expenses = Math.Round(expensesByProperty.GetValueOrDefault(prop.Id, 0m), 2);
                var mgmtFee  = Math.Round(income * (prop.ManagementFeePercent ?? 0m) / 100m, 2);
                net += income - expenses - mgmtFee;
            }

            summaries.Add(new OwnerStatementSummary(o.Id, o.Name, net));
        }

        return summaries;
    }
}
