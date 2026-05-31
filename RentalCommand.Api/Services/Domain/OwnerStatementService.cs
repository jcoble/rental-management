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
        var owner = await _db.Owners
            .AsNoTracking()
            .Where(o => o.PortfolioId == portfolioId && o.Id == ownerId)
            .Select(o => new { o.Id, o.Name })
            .FirstOrDefaultAsync(ct);

        if (owner is null)
            return null;

        // ── Load this owner's properties ────────────────────────────────────────────────────────
        var properties = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.OwnerId == ownerId)
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
        var incomeRows = await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.PaymentType == PaymentType.Rent &&
                p.Status == PaymentStatus.Paid &&
                p.PaidDate != null &&
                p.PaidDate.Value.Year == year &&
                p.Lease != null &&
                propertyIds.Contains(p.Lease!.PropertyId))
            .Select(p => new { p.Lease!.PropertyId, p.Amount })
            .ToListAsync(ct);

        var incomeByProperty = incomeRows
            .GroupBy(r => r.PropertyId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        // ── Expenses in year, keyed by PropertyId ────────────────────────────────────────────────
        var expenseRows = await _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.PropertyId != null &&
                propertyIds.Contains(e.PropertyId!.Value) &&
                e.IncurredAt.Year == year)
            .Select(e => new { PropertyId = e.PropertyId!.Value, e.Amount })
            .ToListAsync(ct);

        var expensesByProperty = expenseRows
            .GroupBy(e => e.PropertyId)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));

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
        // Load all owners that have at least one property in this portfolio.
        var owners = await _db.Owners
            .AsNoTracking()
            .Where(o => o.PortfolioId == portfolioId &&
                        o.Properties.Any(p => p.PortfolioId == portfolioId))
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
                        p.OwnerId != null &&
                        ownerIds.Contains(p.OwnerId!.Value))
            .Select(p => new { p.Id, p.OwnerId, p.ManagementFeePercent })
            .ToListAsync(ct);

        var propertyIds = properties.Select(p => p.Id).ToHashSet();

        // Load income and expenses for those properties.
        var incomeRows = await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.PaymentType == PaymentType.Rent &&
                p.Status == PaymentStatus.Paid &&
                p.PaidDate != null &&
                p.PaidDate.Value.Year == year &&
                p.Lease != null &&
                propertyIds.Contains(p.Lease!.PropertyId))
            .Select(p => new { p.Lease!.PropertyId, p.Amount })
            .ToListAsync(ct);

        var incomeByProperty = incomeRows
            .GroupBy(r => r.PropertyId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        var expenseRows = await _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.PropertyId != null &&
                propertyIds.Contains(e.PropertyId!.Value) &&
                e.IncurredAt.Year == year)
            .Select(e => new { PropertyId = e.PropertyId!.Value, e.Amount })
            .ToListAsync(ct);

        var expensesByProperty = expenseRows
            .GroupBy(e => e.PropertyId)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));

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
