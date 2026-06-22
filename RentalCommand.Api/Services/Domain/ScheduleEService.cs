using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IScheduleEService"/>
public class ScheduleEService : IScheduleEService
{
    private const int UnassignedPropertyId = 0;
    private const string UnassignedPropertyName = "Unassigned";

    private readonly RentalCommandDbContext _db;

    public ScheduleEService(RentalCommandDbContext db)
    {
        _db = db;
    }

    public async Task<ScheduleEReport> GetReportAsync(int portfolioId, int year, CancellationToken ct = default)
    {
        // ── Income ──────────────────────────────────────────────────────────────────────────────
        // Taxable rental income = ACTUAL CASH RECEIVED (§7/§18): Rent + LateFee + tenant Utility
        // reimbursements, Paid (full Amount) or Partial (collected AmountPaid), PaidDate in the year.
        // Security deposits are a liability and are excluded. Grouped by PropertyId via Payment → Lease
        // → Property and summed SQL-side (one row per property); no SUM runs in memory.
        var incomeByProperty = (await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                (p.PaymentType == PaymentType.Rent ||
                 p.PaymentType == PaymentType.LateFee ||
                 p.PaymentType == PaymentType.Utility) &&
                (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Partial) &&
                p.PaidDate != null &&
                p.PaidDate.Value.Year == year)
            .GroupBy(p => p.Lease != null ? p.Lease.PropertyId : UnassignedPropertyId)
            .Select(g => new
            {
                PropertyId = g.Key,
                Total = g.Sum(p => p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount),
            })
            .ToListAsync(ct))
            .ToDictionary(g => g.PropertyId, g => g.Total);

        // ── Expenses ─────────────────────────────────────────────────────────────────────────────
        // Soft-deleted records are excluded by the global query filter. Grouped on (property, category)
        // and summed SQL-side; the flat (propertyId, category, total) rows are reshaped into the nested
        // map in memory, but no SUM runs in memory.
        var expenseCategoryTotals = await _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.IncurredAt.Year == year)
            .GroupBy(e => new { PropertyId = e.PropertyId ?? UnassignedPropertyId, e.Category })
            .Select(g => new { g.Key.PropertyId, g.Key.Category, Total = g.Sum(e => e.Amount) })
            .ToListAsync(ct);

        var expenseTotalsByPropertyCategory = expenseCategoryTotals
            .ToDictionary(r => (r.PropertyId, r.Category), r => r.Total);

        // ── Mortgage interest (from the loan split; principal is NEVER deductible) ─────────────────
        // Σ LoanPayment.InterestAmount for the year, per property (via the loan). Summed SQL-side.
        var interestByProperty = (await _db.LoanPayments
            .AsNoTracking()
            .Where(lp =>
                lp.PortfolioId == portfolioId &&
                lp.Loan != null &&
                lp.DueDate.Year == year)
            .GroupBy(lp => lp.Loan!.PropertyId)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(lp => lp.InterestAmount) })
            .ToListAsync(ct))
            .ToDictionary(r => r.PropertyId, r => r.Total);

        // Properties that have ANY loan (active or not) — their legacy manual MortgageInterest expense
        // category is excluded to avoid double-counting once the loan models the interest.
        var propertiesWithLoan = (await _db.Loans
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId)
            .Select(l => l.PropertyId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        // ── Depreciation (computed per property from its own basis; §6/§18) ────────────────────────
        var propertyBases = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .Select(p => new
            {
                p.Id,
                p.PurchasePrice,
                p.LandValue,
                p.InServiceDate,
                p.ManualAnnualDepreciation,
                p.AccumulatedDepreciation,
            })
            .ToListAsync(ct);

        var depreciationByProperty = new Dictionary<int, DepreciationResult>();
        foreach (var b in propertyBases)
        {
            var result = DepreciationCalculator.AnnualForYear(
                new PropertyDepreciationBasis(b.PurchasePrice, b.LandValue, b.InServiceDate, b.ManualAnnualDepreciation, b.AccumulatedDepreciation),
                year);
            if (result.Amount > 0m)
                depreciationByProperty[b.Id] = result;
        }

        // ── Property names ────────────────────────────────────────────────────────────────────────
        // Collect all property ids that appear in income, expenses, modeled interest, or depreciation
        // (excluding the synthetic 0) — a property with only a loan or only depreciation must still show.
        var realPropertyIds = incomeByProperty.Keys
            .Concat(expenseCategoryTotals.Select(r => r.PropertyId))
            .Concat(interestByProperty.Keys)
            .Concat(depreciationByProperty.Keys)
            .Where(id => id != UnassignedPropertyId)
            .Distinct()
            .ToHashSet();

        var propertyNames = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && realPropertyIds.Contains(p.Id))
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var nameMap = propertyNames.ToDictionary(p => p.Id, p => p.Name);

        // ── Assemble per-property reports ─────────────────────────────────────────────────────────
        var allPropertyIds = propertyNames
            .Select(p => p.Id)
            .Concat(incomeByProperty.ContainsKey(UnassignedPropertyId) ||
                    expenseCategoryTotals.Any(r => r.PropertyId == UnassignedPropertyId)
                ? [UnassignedPropertyId]
                : Array.Empty<int>())
            .ToList();

        var reports = new List<ScheduleEPropertyReport>(allPropertyIds.Count);

        foreach (var propertyId in allPropertyIds)
        {
            var propertyName = propertyId == UnassignedPropertyId
                ? UnassignedPropertyName
                : nameMap.GetValueOrDefault(propertyId, $"Property {propertyId}");

            var income = incomeByProperty.GetValueOrDefault(propertyId, 0m);

            var modeledInterest = interestByProperty.GetValueOrDefault(propertyId, 0m);
            var hasLoan = propertiesWithLoan.Contains(propertyId);
            var depreciation = depreciationByProperty.TryGetValue(propertyId, out var depr) ? depr.Amount : 0m;
            var depreciationIsEstimate = depr.IsFirstYearEstimate && depreciation > 0m;

            // Build category list in enum-declared order; omit zero amounts. Deterministic legacy
            // double-count exclusion (§10/§18): when a loan exists for the property, drop the manual
            // MortgageInterest category (the modeled interest replaces it); when computed depreciation
            // applies, drop the manual Depreciation category.
            var categories = new List<ScheduleECategoryAmount>();
            foreach (ScheduleECategory cat in Enum.GetValues<ScheduleECategory>())
            {
                if (cat == ScheduleECategory.MortgageInterest && hasLoan)
                    continue;
                if (cat == ScheduleECategory.Depreciation && depreciation > 0m)
                    continue;

                if (expenseTotalsByPropertyCategory.TryGetValue((propertyId, cat), out var amount) && amount != 0m)
                    categories.Add(new ScheduleECategoryAmount(cat.ToString(), amount));
            }

            // Fold the modeled deductions into the category breakdown (so the CSV/packet show them and
            // the totals net correctly): mortgage interest from the loan split (principal excluded) and
            // the computed depreciation.
            if (modeledInterest != 0m)
                categories.Add(new ScheduleECategoryAmount(ScheduleECategory.MortgageInterest.ToString(), modeledInterest));
            if (depreciation != 0m)
                categories.Add(new ScheduleECategoryAmount(ScheduleECategory.Depreciation.ToString(), depreciation));

            var propertyTotalExpenses = categories.Sum(c => c.Amount);

            reports.Add(new ScheduleEPropertyReport(
                PropertyId: propertyId,
                PropertyName: propertyName,
                RentalIncome: income,
                ExpensesByCategory: categories,
                TotalExpenses: propertyTotalExpenses,
                NetIncome: income - propertyTotalExpenses,
                MortgageInterest: modeledInterest,
                Depreciation: depreciation,
                DepreciationIsFirstYearEstimate: depreciationIsEstimate));
        }

        var totalIncome = reports.Sum(r => r.RentalIncome);
        var totalExpenses = reports.Sum(r => r.TotalExpenses);

        return new ScheduleEReport
        {
            Year = year,
            Properties = reports,
            TotalRentalIncome = totalIncome,
            TotalExpenses = totalExpenses,
            NetIncome = totalIncome - totalExpenses,
        };
    }
}
