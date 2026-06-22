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

    public async Task<ScheduleEReport> GetReportAsync(int portfolioId, int year, int? propertyId = null, CancellationToken ct = default)
    {
        if (propertyId.HasValue)
        {
            var inPortfolio = await _db.Properties
                .AsNoTracking()
                .AnyAsync(p => p.PortfolioId == portfolioId && p.Id == propertyId.Value, ct);
            if (!inPortfolio)
                return EmptyReport(year);
        }

        // ── Income ──────────────────────────────────────────────────────────────────────────────
        // Taxable rental income = ACTUAL CASH RECEIVED (§7/§18): Rent + LateFee + tenant Utility
        // reimbursements, Paid (full Amount) or Partial (collected AmountPaid), PaidDate in the year.
        // Security deposits are a liability and are excluded. Grouped by PropertyId via Payment → Lease
        // → Property and summed SQL-side (one row per property); no SUM runs in memory.
        var incomeQuery = _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                (p.PaymentType == PaymentType.Rent ||
                 p.PaymentType == PaymentType.LateFee ||
                 p.PaymentType == PaymentType.Utility) &&
                (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Partial) &&
                p.PaidDate != null &&
                p.PaidDate.Value.Year == year);
        if (propertyId.HasValue)
            incomeQuery = incomeQuery.Where(p => p.Lease != null && p.Lease.PropertyId == propertyId.Value);

        var totalRentalIncome = await incomeQuery
            .SumAsync(p => (decimal?)(p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount), ct) ?? 0m;

        var incomeByProperty = (await incomeQuery
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
        var expenseQuery = _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.IncurredAt.Year == year);
        if (propertyId.HasValue)
            expenseQuery = expenseQuery.Where(e => e.PropertyId == propertyId.Value);

        var expenseCategoryTotals = await expenseQuery
            .GroupBy(e => new { PropertyId = e.PropertyId ?? UnassignedPropertyId, e.Category })
            .Select(g => new { g.Key.PropertyId, g.Key.Category, Total = g.Sum(e => e.Amount) })
            .ToListAsync(ct);

        var expenseTotalsByPropertyCategory = expenseCategoryTotals
            .ToDictionary(r => (r.PropertyId, r.Category), r => r.Total);

        // ── Mortgage interest (from the loan split; principal is NEVER deductible) ─────────────────
        // Σ LoanPayment.InterestAmount for the year, per property (via the loan). Summed SQL-side.
        var loanPaymentQuery = _db.LoanPayments
            .AsNoTracking()
            .Where(lp =>
                lp.PortfolioId == portfolioId &&
                lp.Loan != null &&
                lp.DueDate.Year == year);
        if (propertyId.HasValue)
            loanPaymentQuery = loanPaymentQuery.Where(lp => lp.Loan!.PropertyId == propertyId.Value);

        var totalModeledInterest = await loanPaymentQuery
            .SumAsync(lp => (decimal?)lp.InterestAmount, ct) ?? 0m;

        var interestByProperty = (await loanPaymentQuery
            .GroupBy(lp => lp.Loan!.PropertyId)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(lp => lp.InterestAmount) })
            .ToListAsync(ct))
            .ToDictionary(r => r.PropertyId, r => r.Total);

        // Properties that have ANY loan (active or not) — their legacy manual MortgageInterest expense
        // category is excluded to avoid double-counting once the loan models the interest.
        var loanQuery = _db.Loans
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId);
        if (propertyId.HasValue)
            loanQuery = loanQuery.Where(l => l.PropertyId == propertyId.Value);

        var propertiesWithLoan = (await loanQuery
            .Select(l => l.PropertyId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        // ── Depreciation (computed per property from its own basis; §6/§18) ────────────────────────
        var propertyBasisQuery = _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId);
        if (propertyId.HasValue)
            propertyBasisQuery = propertyBasisQuery.Where(p => p.Id == propertyId.Value);

        var propertyBases = await propertyBasisQuery
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

        var propertiesWithLoanIds = propertiesWithLoan.ToArray();
        var depreciationPropertyIds = depreciationByProperty
            .Where(kvp => kvp.Value.Amount > 0m)
            .Select(kvp => kvp.Key)
            .ToArray();

        var deductibleExpenseQuery = expenseQuery
            .Where(e =>
                !(e.Category == ScheduleECategory.MortgageInterest &&
                  e.PropertyId != null &&
                  propertiesWithLoanIds.Contains(e.PropertyId.Value)) &&
                !(e.Category == ScheduleECategory.Depreciation &&
                  e.PropertyId != null &&
                  depreciationPropertyIds.Contains(e.PropertyId.Value)));

        var deductibleExpensesByProperty = (await deductibleExpenseQuery
            .GroupBy(e => e.PropertyId ?? UnassignedPropertyId)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(e => e.Amount) })
            .ToListAsync(ct))
            .ToDictionary(r => r.PropertyId, r => r.Total);

        var totalDeductibleExpenses = await deductibleExpenseQuery
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var totalDepreciation = depreciationByProperty.Values.Sum(d => d.Amount);

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
            .Concat(!propertyId.HasValue &&
                    (incomeByProperty.ContainsKey(UnassignedPropertyId) ||
                     expenseCategoryTotals.Any(r => r.PropertyId == UnassignedPropertyId))
                ? [UnassignedPropertyId]
                : Array.Empty<int>())
            .ToList();

        var reports = new List<ScheduleEPropertyReport>(allPropertyIds.Count);

        foreach (var reportPropertyId in allPropertyIds)
        {
            var propertyName = reportPropertyId == UnassignedPropertyId
                ? UnassignedPropertyName
                : nameMap.GetValueOrDefault(reportPropertyId, $"Property {reportPropertyId}");

            var income = incomeByProperty.GetValueOrDefault(reportPropertyId, 0m);

            var modeledInterest = interestByProperty.GetValueOrDefault(reportPropertyId, 0m);
            var hasLoan = propertiesWithLoan.Contains(reportPropertyId);
            var depreciation = depreciationByProperty.TryGetValue(reportPropertyId, out var depr) ? depr.Amount : 0m;
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

                if (expenseTotalsByPropertyCategory.TryGetValue((reportPropertyId, cat), out var amount) && amount != 0m)
                    categories.Add(new ScheduleECategoryAmount(cat.ToString(), amount));
            }

            // Fold the modeled deductions into the category breakdown (so the CSV/packet show them and
            // the totals net correctly): mortgage interest from the loan split (principal excluded) and
            // the computed depreciation.
            if (modeledInterest != 0m)
                categories.Add(new ScheduleECategoryAmount(ScheduleECategory.MortgageInterest.ToString(), modeledInterest));
            if (depreciation != 0m)
                categories.Add(new ScheduleECategoryAmount(ScheduleECategory.Depreciation.ToString(), depreciation));

            var propertyTotalExpenses = deductibleExpensesByProperty.GetValueOrDefault(reportPropertyId, 0m) +
                                        modeledInterest +
                                        depreciation;

            reports.Add(new ScheduleEPropertyReport(
                PropertyId: reportPropertyId,
                PropertyName: propertyName,
                RentalIncome: income,
                ExpensesByCategory: categories,
                TotalExpenses: propertyTotalExpenses,
                NetIncome: income - propertyTotalExpenses,
                MortgageInterest: modeledInterest,
                Depreciation: depreciation,
                DepreciationIsFirstYearEstimate: depreciationIsEstimate));
        }

        var totalExpenses = totalDeductibleExpenses + totalModeledInterest + totalDepreciation;

        return new ScheduleEReport
        {
            Year = year,
            Properties = reports,
            TotalRentalIncome = totalRentalIncome,
            TotalExpenses = totalExpenses,
            NetIncome = totalRentalIncome - totalExpenses,
        };
    }

    private static ScheduleEReport EmptyReport(int year) => new()
    {
        Year = year,
        Properties = [],
        TotalRentalIncome = 0m,
        TotalExpenses = 0m,
        NetIncome = 0m,
    };
}
