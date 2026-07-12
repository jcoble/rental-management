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
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEndExclusive = yearStart.AddYears(1);
        var yearStartDate = new DateOnly(year, 1, 1);
        var yearEndExclusiveDate = yearStartDate.AddYears(1);

        if (propertyId.HasValue)
        {
            var inPortfolio = await _db.Properties
                .AsNoTracking()
                .AnyAsync(p => p.PortfolioId == portfolioId && p.Id == propertyId.Value, ct);
            if (!inPortfolio)
                return EmptyReport(year);
        }

        // ── Income ──────────────────────────────────────────────────────────────────────────────
        // Taxable income is projected from tenant cash receipts plus the separate pre-tenancy
        // application subledger. The UNION, filters, correlated sums, and total all remain SQL-side.
        var tenantIncomeQuery =
            from allocation in _db.TenantLedgerAllocations.AsNoTracking()
            join receipt in _db.TenantLedgerEntries.AsNoTracking()
                on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.CreditEntryId }
                equals new { receipt.PortfolioId, receipt.TenantAccountId, receipt.Id }
            join charge in _db.TenantLedgerEntries.AsNoTracking()
                on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.DebitEntryId }
                equals new { charge.PortfolioId, charge.TenantAccountId, charge.Id }
            join account in _db.TenantAccounts.AsNoTracking()
                on new { allocation.PortfolioId, Id = allocation.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            where allocation.PortfolioId == portfolioId
                && receipt.EntryType == TenantLedgerEntryType.PaymentReceipt
                && receipt.EffectiveOn >= yearStartDate
                && receipt.EffectiveOn < yearEndExclusiveDate
                && (charge.EntryType == TenantLedgerEntryType.RentCharge
                    || charge.EntryType == TenantLedgerEntryType.LateFeeCharge
                    || charge.EntryType == TenantLedgerEntryType.AddendumCharge
                    || charge.EntryType == TenantLedgerEntryType.ManualCharge)
            select new
            {
                PropertyId = (int?)management.PropertyId,
                Amount = allocation.Amount,
            };
        var applicationIncomeQuery = _db.ApplicationFinancialEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.EffectiveOn >= yearStartDate
                && entry.EffectiveOn < yearEndExclusiveDate)
            .Select(entry => new
            {
                entry.PropertyId,
                Amount = entry.Direction == ApplicationFinancialDirection.Increase
                    ? entry.Amount
                    : -entry.Amount,
            });
        var incomeQuery = tenantIncomeQuery.Concat(applicationIncomeQuery);
        if (propertyId.HasValue)
            incomeQuery = incomeQuery.Where(row => row.PropertyId == propertyId.Value);

        var totalRentalIncome = await incomeQuery
            .SumAsync(row => (decimal?)row.Amount, ct) ?? 0m;

        // ── Expenses ─────────────────────────────────────────────────────────────────────────────
        // Soft-deleted records are excluded by the global query filter. Grouped on (property, category)
        // and summed SQL-side; the flat (propertyId, category, total) rows are reshaped into the nested
        // map in memory. Property totals are projected below with each property row.
        var expenseQuery = _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.CapitalizedAssetId == null &&
                e.IncurredAt >= yearStart &&
                e.IncurredAt < yearEndExclusive);
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
                lp.DueDate >= yearStart &&
                lp.DueDate < yearEndExclusive);
        if (propertyId.HasValue)
            loanPaymentQuery = loanPaymentQuery.Where(lp => lp.Loan!.PropertyId == propertyId.Value);

        var totalModeledInterest = await loanPaymentQuery
            .SumAsync(lp => (decimal?)lp.InterestAmount, ct) ?? 0m;

        // Properties that have ANY loan (active or not) — their legacy manual MortgageInterest expense
        // category is excluded to avoid double-counting once the loan models the interest.
        var loanQuery = _db.Loans
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId);
        if (propertyId.HasValue)
            loanQuery = loanQuery.Where(l => l.PropertyId == propertyId.Value);
        var loanPropertyIdsQuery = loanQuery.Select(l => l.PropertyId).Distinct();

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

        var capitalAssetQuery = _db.CapitalAssets
            .AsNoTracking()
            .Where(a =>
                a.PortfolioId == portfolioId &&
                a.InServiceDate < yearEndExclusive &&
                a.DisposedOnDate == null);
        if (propertyId.HasValue)
            capitalAssetQuery = capitalAssetQuery.Where(a => a.PropertyId == propertyId.Value);

        var capitalAssets = await capitalAssetQuery
            .Select(a => new
            {
                a.PropertyId,
                a.CostBasis,
                a.InServiceDate,
                a.Method,
                a.RecoveryYears,
                a.Convention,
                a.AccumulatedDepreciation,
            })
            .ToListAsync(ct);

        foreach (var asset in capitalAssets)
        {
            var result = DepreciationCalculator.AnnualForYear(
                asset.CostBasis,
                asset.InServiceDate,
                asset.Method,
                asset.RecoveryYears,
                asset.Convention,
                asset.AccumulatedDepreciation,
                year);
            if (result.Amount > 0m)
                AddDepreciation(depreciationByProperty, asset.PropertyId, result);
        }

        var depreciationPropertyIds = depreciationByProperty
            .Where(kvp => kvp.Value.Amount > 0m)
            .Select(kvp => kvp.Key)
            .ToArray();

        var deductibleExpenseQuery = expenseQuery
            .Where(e =>
                !(e.Category == ScheduleECategory.MortgageInterest &&
                  e.PropertyId != null &&
                  loanPropertyIdsQuery.Contains(e.PropertyId.Value)) &&
                !(e.Category == ScheduleECategory.Depreciation &&
                  e.PropertyId != null &&
                  depreciationPropertyIds.Contains(e.PropertyId.Value)));

        var totalDeductibleExpenses = await deductibleExpenseQuery
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var totalDepreciation = depreciationByProperty.Values.Sum(d => d.Amount);

        // ── Property rows ─────────────────────────────────────────────────────────────────────────
        // Project the Schedule E row facts with the ordered property rows. Income, modeled interest,
        // and deductible expenses are correlated SQL sums, so no aggregate dictionaries are joined back
        // to properties in memory. Depreciation remains the existing app-domain calculation from basis.
        var reportPropertyQuery = _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId);
        if (propertyId.HasValue)
            reportPropertyQuery = reportPropertyQuery.Where(p => p.Id == propertyId.Value);

        var propertyRows = await reportPropertyQuery
            .Where(p =>
                incomeQuery.Any(income => income.PropertyId == p.Id) ||
                expenseQuery.Any(e => e.PropertyId == p.Id) ||
                loanPaymentQuery.Any(lp => lp.Loan != null && lp.Loan.PropertyId == p.Id) ||
                depreciationPropertyIds.Contains(p.Id))
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                Income = incomeQuery
                    .Where(income => income.PropertyId == p.Id)
                    .Sum(income => (decimal?)income.Amount) ?? 0m,
                ModeledInterest = loanPaymentQuery
                    .Where(lp => lp.Loan != null && lp.Loan.PropertyId == p.Id)
                    .Sum(lp => (decimal?)lp.InterestAmount) ?? 0m,
                HasLoan = loanQuery.Any(l => l.PropertyId == p.Id),
                DeductibleExpenses = deductibleExpenseQuery
                    .Where(e => e.PropertyId == p.Id)
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
            })
            .ToListAsync(ct);

        // ── Assemble per-property reports ─────────────────────────────────────────────────────────
        var unassignedIncome = propertyId.HasValue
            ? 0m
            : await incomeQuery
                .Where(income => income.PropertyId == null)
                .SumAsync(income => (decimal?)income.Amount, ct) ?? 0m;
        var unassignedDeductibleExpenses = propertyId.HasValue
            ? 0m
            : await deductibleExpenseQuery
                .Where(e => e.PropertyId == null)
                .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;
        var hasUnassignedRow = !propertyId.HasValue &&
                               (unassignedIncome != 0m ||
                                unassignedDeductibleExpenses != 0m ||
                                expenseCategoryTotals.Any(r => r.PropertyId == UnassignedPropertyId));

        var reports = new List<ScheduleEPropertyReport>(propertyRows.Count + (hasUnassignedRow ? 1 : 0));

        foreach (var prop in propertyRows)
        {
            var depreciation = depreciationByProperty.TryGetValue(prop.Id, out var depr) ? depr.Amount : 0m;
            var depreciationIsEstimate = depr.IsFirstYearEstimate && depreciation > 0m;

            // Build category list in enum-declared order; omit zero amounts. Deterministic legacy
            // double-count exclusion (§10/§18): when a loan exists for the property, drop the manual
            // MortgageInterest category (the modeled interest replaces it); when computed depreciation
            // applies, drop the manual Depreciation category.
            var categories = new List<ScheduleECategoryAmount>();
            foreach (ScheduleECategory cat in Enum.GetValues<ScheduleECategory>())
            {
                if (cat == ScheduleECategory.MortgageInterest && prop.HasLoan)
                    continue;
                if (cat == ScheduleECategory.Depreciation && depreciation > 0m)
                    continue;

                if (expenseTotalsByPropertyCategory.TryGetValue((prop.Id, cat), out var amount) && amount != 0m)
                    categories.Add(new ScheduleECategoryAmount(cat.ToString(), amount));
            }

            // Fold the modeled deductions into the category breakdown (so the CSV/packet show them and
            // the totals net correctly): mortgage interest from the loan split (principal excluded) and
            // the computed depreciation.
            if (prop.ModeledInterest != 0m)
                categories.Add(new ScheduleECategoryAmount(ScheduleECategory.MortgageInterest.ToString(), prop.ModeledInterest));
            if (depreciation != 0m)
                categories.Add(new ScheduleECategoryAmount(ScheduleECategory.Depreciation.ToString(), depreciation));

            var propertyTotalExpenses = prop.DeductibleExpenses +
                                        prop.ModeledInterest +
                                        depreciation;

            reports.Add(new ScheduleEPropertyReport(
                PropertyId: prop.Id,
                PropertyName: prop.Name,
                RentalIncome: prop.Income,
                ExpensesByCategory: categories,
                TotalExpenses: propertyTotalExpenses,
                NetIncome: prop.Income - propertyTotalExpenses,
                MortgageInterest: prop.ModeledInterest,
                Depreciation: depreciation,
                DepreciationIsFirstYearEstimate: depreciationIsEstimate));
        }

        if (hasUnassignedRow)
        {
            var categories = new List<ScheduleECategoryAmount>();
            foreach (ScheduleECategory cat in Enum.GetValues<ScheduleECategory>())
            {
                if (expenseTotalsByPropertyCategory.TryGetValue((UnassignedPropertyId, cat), out var amount) && amount != 0m)
                    categories.Add(new ScheduleECategoryAmount(cat.ToString(), amount));
            }

            reports.Add(new ScheduleEPropertyReport(
                PropertyId: UnassignedPropertyId,
                PropertyName: UnassignedPropertyName,
                RentalIncome: unassignedIncome,
                ExpensesByCategory: categories,
                TotalExpenses: unassignedDeductibleExpenses,
                NetIncome: unassignedIncome - unassignedDeductibleExpenses,
                MortgageInterest: 0m,
                Depreciation: 0m,
                DepreciationIsFirstYearEstimate: false));
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

    private static void AddDepreciation(
        IDictionary<int, DepreciationResult> byProperty, int propertyId, DepreciationResult result)
    {
        if (byProperty.TryGetValue(propertyId, out var existing))
        {
            byProperty[propertyId] = new DepreciationResult(
                existing.Amount + result.Amount,
                existing.IsFirstYearEstimate || result.IsFirstYearEstimate);
            return;
        }

        byProperty[propertyId] = result;
    }
}
