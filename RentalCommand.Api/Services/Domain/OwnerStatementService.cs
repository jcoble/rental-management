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

        // ── Property lines ─────────────────────────────────────────────────────────────────────
        // Filter/sort each owned property and project its rent/expense aggregates in one translated
        // property query. DTO math/rounding stays post-query; no payment/expense rows are materialized.
        var propertyRows = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.OwnerEntityId == ownerId)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.ManagementFeePercent,
                RentalIncome = _db.Payments
                    .Where(pay =>
                        pay.PortfolioId == portfolioId &&
                        pay.PaymentType == PaymentType.Rent &&
                        pay.Status == PaymentStatus.Paid &&
                        pay.PaidDate != null &&
                        pay.PaidDate.Value.Year == year &&
                        pay.Lease != null &&
                        pay.Lease.PropertyId == p.Id)
                    .Sum(pay => (decimal?)pay.Amount) ?? 0m,
                Expenses = _db.Expenses
                    .Where(e =>
                        e.PortfolioId == portfolioId &&
                        e.PropertyId == p.Id &&
                        e.Status == ExpenseStatus.Paid &&
                        (e.PaidAt ?? e.IncurredAt).Year == year)
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
            })
            .ToListAsync(ct);

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
            };
        }

        // ── Assemble per-property lines ──────────────────────────────────────────────────────────
        var lines = new List<OwnerStatementPropertyLine>(propertyRows.Count);

        foreach (var prop in propertyRows)
        {
            var income   = Math.Round(prop.RentalIncome, 2);
            var expenses = Math.Round(prop.Expenses, 2);
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

        var totals = await OwnerPropertyNetRows(portfolioId, year)
            .Where(p => p.OwnerId == ownerId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalIncome = g.Sum(p => p.RentalIncome),
                TotalExpenses = g.Sum(p => p.Expenses),
                TotalManagementFee = g.Sum(p => p.RentalIncome * p.ManagementFeePercent / 100m),
                TotalNetToOwner = g.Sum(p =>
                    p.RentalIncome -
                    p.Expenses -
                    (p.RentalIncome * p.ManagementFeePercent / 100m)),
            })
            .SingleAsync(ct);

        return new OwnerStatementReport
        {
            OwnerId            = owner.Id,
            OwnerName          = owner.Name,
            Year               = year,
            Properties         = lines,
            TotalIncome        = Math.Round(totals.TotalIncome, 2),
            TotalExpenses      = Math.Round(totals.TotalExpenses, 2),
            TotalManagementFee = Math.Round(totals.TotalManagementFee, 2),
            TotalNetToOwner    = Math.Round(totals.TotalNetToOwner, 2),
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OwnerStatementSummary>> ListOwnersWithNetAsync(
        int portfolioId, int year, CancellationToken ct = default)
    {
        var propertyNetRows = OwnerPropertyNetRows(portfolioId, year);

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
            })
            .OrderBy(o => o.OwnerName)
            .ToListAsync(ct);

        return summaries
            .Select(s => new OwnerStatementSummary(s.OwnerId, s.OwnerName, s.NetToOwner))
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
        return _db.Properties
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.OwnerEntityId != null &&
                p.OwnerEntity != null)
            .Select(p => new OwnerPropertyNetRow
            {
                OwnerId = p.OwnerEntityId!.Value,
                OwnerName = p.OwnerEntity!.Name,
                ManagementFeePercent = p.ManagementFeePercent ?? 0m,
                RentalIncome = _db.Payments
                    .Where(pay =>
                        pay.PortfolioId == portfolioId &&
                        pay.PaymentType == PaymentType.Rent &&
                        pay.Status == PaymentStatus.Paid &&
                        pay.PaidDate != null &&
                        pay.PaidDate.Value.Year == year &&
                        pay.Lease != null &&
                        pay.Lease.PropertyId == p.Id)
                    .Sum(pay => (decimal?)pay.Amount) ?? 0m,
                Expenses = _db.Expenses
                    .Where(e =>
                        e.PortfolioId == portfolioId &&
                        e.PropertyId == p.Id &&
                        e.Status == ExpenseStatus.Paid &&
                        (e.PaidAt ?? e.IncurredAt).Year == year)
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
            });
    }

    private sealed class OwnerPropertyNetRow
    {
        public int OwnerId { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public decimal ManagementFeePercent { get; set; }
        public decimal RentalIncome { get; set; }
        public decimal Expenses { get; set; }
    }
}
