using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IScheduleEService"/>
public class ScheduleEService : IScheduleEService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public ScheduleEService(RentalCommandDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ScheduleEReport> GetReportAsync(
        WorkspaceReadScope scope,
        int year,
        int? propertyId = null,
        CancellationToken ct = default)
    {
        var properties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                CapabilityKeys.ReportsRead,
                _timeProvider.GetUtcNow().UtcDateTime);
        return await GetReportCoreAsync(
            scope, year, properties, propertyId, ct);
    }

    private async Task<ScheduleEReport> GetReportCoreAsync(
        WorkspaceReadScope scope,
        int year,
        IQueryable<Property> authorizedProperties,
        int? propertyId,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEndExclusive = yearStart.AddYears(1);
        var yearStartDate = new DateOnly(year, 1, 1);
        var yearEndExclusiveDate = yearStartDate.AddYears(1);
        var workspaceAdministratorPortfolio = WorkspaceAdministratorPortfolio(scope, utcNow);

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
                && authorizedProperties.Any(property => property.Id == management.PropertyId)
                && receipt.EntryType == TenantLedgerEntryType.PaymentReceipt
                && receipt.EffectiveOn >= yearStartDate
                && receipt.EffectiveOn < yearEndExclusiveDate
                && (charge.EntryType == TenantLedgerEntryType.RentCharge
                    || charge.EntryType == TenantLedgerEntryType.LateFeeCharge
                    || charge.EntryType == TenantLedgerEntryType.AddendumCharge
                    || charge.EntryType == TenantLedgerEntryType.ManualCharge)
            select new ScheduleEIncomeComponent
            {
                PropertyId = (int?)management.PropertyId,
                Amount = allocation.Amount,
            };
        var applicationIncomeFactsQuery = _db.ApplicationFinancialEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.EffectiveOn >= yearStartDate
                && entry.EffectiveOn < yearEndExclusiveDate)
            .Select(entry => new ScheduleEIncomeComponent
            {
                PropertyId = entry.PropertyId != null
                    ? entry.PropertyId
                    : entry.UnitId != null
                        ? _db.Units.IgnoreQueryFilters()
                            .Where(unit =>
                                unit.PortfolioId == portfolioId &&
                                unit.Id == entry.UnitId.Value)
                            .Select(unit => (int?)unit.PropertyId)
                            .FirstOrDefault()
                        : null,
                Amount = entry.Direction == ApplicationFinancialDirection.Increase
                    ? entry.Amount
                    : -entry.Amount,
            });
        var applicationIncomeQuery = applicationIncomeFactsQuery
            .Where(entry =>
                entry.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == entry.PropertyId.Value));
        var incomeQuery = tenantIncomeQuery.Concat(applicationIncomeQuery);
        if (propertyId.HasValue)
            incomeQuery = incomeQuery.Where(row => row.PropertyId == propertyId.Value);

        var unallocatedIncomeQuery = applicationIncomeFactsQuery
            .Where(entry => entry.PropertyId == null);

        // ── Expenses ─────────────────────────────────────────────────────────────────────────────
        // Resolve the expense's canonical operational context in SQL. Work-order receipts inherit the
        // work order's Property, direct Unit expenses inherit the Unit's Property, and a direct Property
        // scope is used otherwise. The blueprint's typed ExpenseAllocation rows do not exist in the
        // current schema yet, so genuinely portfolio-scoped rows remain unallocated and are reconciled
        // explicitly below instead of being copied or guessed onto a Property.
        var expenseFactsQuery = _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.CapitalizedAssetId == null &&
                e.Status == ExpenseStatus.Paid &&
                (e.PaidAt ?? e.IncurredAt) >= yearStart &&
                (e.PaidAt ?? e.IncurredAt) < yearEndExclusive)
            .Select(expense => new ScheduleEExpenseFact
            {
                PropertyId = expense.WorkOrderId != null
                    ? _db.WorkOrders.IgnoreQueryFilters()
                        .Where(workOrder =>
                            workOrder.PortfolioId == portfolioId &&
                            workOrder.Id == expense.WorkOrderId.Value)
                        .Select(workOrder => (int?)workOrder.PropertyId)
                        .FirstOrDefault()
                    : expense.UnitId != null
                        ? _db.Units.IgnoreQueryFilters()
                            .Where(unit =>
                                unit.PortfolioId == portfolioId &&
                                unit.Id == expense.UnitId.Value)
                            .Select(unit => (int?)unit.PropertyId)
                            .FirstOrDefault()
                        : expense.PropertyId,
                Category = expense.Category,
                Amount = expense.Amount,
            });

        var expenseQuery = expenseFactsQuery
            .Where(expense =>
                expense.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == expense.PropertyId.Value));
        if (propertyId.HasValue)
            expenseQuery = expenseQuery.Where(expense => expense.PropertyId == propertyId.Value);

        var unallocatedExpenseQuery = expenseFactsQuery
            .Where(expense => expense.PropertyId == null);

        // ── Mortgage interest (from the loan split; principal is NEVER deductible) ─────────────────
        // Σ LoanPayment.InterestAmount for the year, per property (via the loan). Summed SQL-side.
        var loanPaymentQuery = LoanPaymentEffectiveQuery.From(_db)
            .Where(lp =>
                lp.PortfolioId == portfolioId &&
                _db.Loans.Any(loan =>
                    loan.Id == lp.LoanId &&
                    authorizedProperties.Any(property => property.Id == loan.PropertyId)) &&
                lp.DueDate >= yearStart &&
                lp.DueDate < yearEndExclusive);
        if (propertyId.HasValue)
            loanPaymentQuery = loanPaymentQuery.Where(lp =>
                _db.Loans.Any(loan => loan.Id == lp.LoanId && loan.PropertyId == propertyId.Value));

        // Properties that have ANY loan (active or not) — their legacy manual MortgageInterest expense
        // category is excluded to avoid double-counting once the loan models the interest.
        var loanQuery = _db.Loans
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId &&
                authorizedProperties.Any(property => property.Id == l.PropertyId));
        if (propertyId.HasValue)
            loanQuery = loanQuery.Where(l => l.PropertyId == propertyId.Value);
        var loanPropertyIdsQuery = loanQuery.Select(l => l.PropertyId).Distinct();

        // ── Depreciation (one SQL union/group/total over property and asset bases; §6/§18) ────────
        var depreciationQuery = ScheduleEDepreciationQuery
            .Build(_db, portfolioId, year, authorizedProperties, propertyId);

        var deductibleExpenseQuery = expenseQuery
            .Where(e =>
                !(e.Category == ScheduleECategory.MortgageInterest &&
                  e.PropertyId != null &&
                  loanPropertyIdsQuery.Contains(e.PropertyId.Value)) &&
                !(e.Category == ScheduleECategory.Depreciation &&
                  e.PropertyId != null &&
                  depreciationQuery.Any(depreciation =>
                      depreciation.PropertyId == e.PropertyId.Value)));

        var categoryComponents = deductibleExpenseQuery
            .Select(expense => new ScheduleECategoryComponent
            {
                PropertyId = expense.PropertyId!.Value,
                Category = expense.Category,
                Amount = expense.Amount,
            })
            .Concat(loanPaymentQuery.Select(payment => new ScheduleECategoryComponent
            {
                PropertyId = payment.PropertyId,
                Category = ScheduleECategory.MortgageInterest,
                Amount = payment.InterestAmount,
            }))
            .Concat(depreciationQuery.Select(depreciation => new ScheduleECategoryComponent
            {
                PropertyId = depreciation.PropertyId,
                Category = ScheduleECategory.Depreciation,
                Amount = depreciation.Amount,
            }));

        var categoryTotalsQuery = categoryComponents
            .GroupBy(component => new { component.PropertyId, component.Category })
            .Select(group => new ScheduleECategorySqlRow
            {
                PropertyId = group.Key.PropertyId,
                Category = group.Key.Category,
                Amount = group.Sum(component => component.Amount),
            });

        // ── Property rows ─────────────────────────────────────────────────────────────────────────
        // Project one flat property/category relation. Projecting the grouped category query as a nested
        // collection is not translatable because its depreciation branch is a keyless projection and EF
        // cannot derive a stable collection identifier. The left join keeps the same work in one SQL
        // statement while also retaining income-only properties through a null category row.
        var reportPropertyQuery = authorizedProperties;
        if (propertyId.HasValue)
            reportPropertyQuery = reportPropertyQuery.Where(p => p.Id == propertyId.Value);

        var propertyFactsQuery = reportPropertyQuery
            .Where(property =>
                incomeQuery.Any(income => income.PropertyId == property.Id) ||
                categoryTotalsQuery.Any(category => category.PropertyId == property.Id))
            .Select(property => new ScheduleEPropertySqlRow
            {
                PropertyId = property.Id,
                PropertyName = property.Name,
                Income = incomeQuery
                    .Where(income => income.PropertyId == property.Id)
                    .Sum(income => (decimal?)income.Amount) ?? 0m,
                ModeledInterest = loanPaymentQuery
                    .Where(payment => payment.PropertyId == property.Id)
                    .Sum(payment => (decimal?)payment.InterestAmount) ?? 0m,
                Depreciation = depreciationQuery
                    .Where(depreciation => depreciation.PropertyId == property.Id)
                    .Select(depreciation => (decimal?)depreciation.Amount)
                    .FirstOrDefault() ?? 0m,
                DepreciationIsFirstYearEstimate = depreciationQuery
                    .Where(depreciation => depreciation.PropertyId == property.Id)
                    .Select(depreciation => depreciation.IsFirstYearEstimate)
                    .FirstOrDefault(),
                TotalExpenses = categoryTotalsQuery
                    .Where(category => category.PropertyId == property.Id)
                    .Sum(category => (decimal?)category.Amount) ?? 0m,
                NetIncome = (incomeQuery
                        .Where(income => income.PropertyId == property.Id)
                        .Sum(income => (decimal?)income.Amount) ?? 0m) -
                    (categoryTotalsQuery
                        .Where(category => category.PropertyId == property.Id)
                        .Sum(category => (decimal?)category.Amount) ?? 0m),
            });
        var nonZeroCategoryTotalsQuery = categoryTotalsQuery
            .Where(category => category.Amount != 0m);
        var allocatedCategoryTotalsQuery = categoryTotalsQuery
            .GroupBy(category => category.Category)
            .Select(group => new ScheduleEReportCategorySqlRow
            {
                Category = group.Key,
                Amount = group.Sum(category => category.Amount),
            })
            .Where(category => category.Amount != 0m);
        var unallocatedCategoryTotalsQuery = unallocatedExpenseQuery
            .GroupBy(expense => expense.Category)
            .Select(group => new ScheduleEReportCategorySqlRow
            {
                Category = group.Key,
                Amount = group.Sum(expense => expense.Amount),
            })
            .Where(category => category.Amount != 0m);

        var reportTotalsQuery = _db.Portfolios
            .AsNoTracking()
            .Where(portfolio =>
                portfolio.Id == portfolioId &&
                (authorizedProperties.Any() || workspaceAdministratorPortfolio.Any()))
            .Select(_ => new ScheduleEReportTotalsSqlRow
            {
                TotalRentalIncome = incomeQuery.Sum(income => (decimal?)income.Amount) ?? 0m,
                TotalExpenses = categoryTotalsQuery.Sum(category => (decimal?)category.Amount) ?? 0m,
                CanViewUnallocated = !propertyId.HasValue && workspaceAdministratorPortfolio.Any(),
                UnallocatedIncomeEntryCount = !propertyId.HasValue && workspaceAdministratorPortfolio.Any()
                    ? unallocatedIncomeQuery.Count()
                    : 0,
                UnallocatedRentalIncome = !propertyId.HasValue && workspaceAdministratorPortfolio.Any()
                    ? unallocatedIncomeQuery.Sum(income => (decimal?)income.Amount) ?? 0m
                    : 0m,
                UnallocatedExpenseCount = !propertyId.HasValue && workspaceAdministratorPortfolio.Any()
                    ? unallocatedExpenseQuery.Count()
                    : 0,
                UnallocatedTotalExpenses = !propertyId.HasValue && workspaceAdministratorPortfolio.Any()
                    ? unallocatedExpenseQuery.Sum(expense => (decimal?)expense.Amount) ?? 0m
                    : 0m,
            });

        var propertyFlatRowsQuery =
            from totals in reportTotalsQuery
            from property in propertyFactsQuery
            join category in nonZeroCategoryTotalsQuery
                on property.PropertyId equals category.PropertyId into propertyCategories
            from category in propertyCategories.DefaultIfEmpty()
            select new ScheduleEFlatSqlRow
            {
                RowKind = ScheduleEFlatRowKind.Property,
                PropertyId = property.PropertyId,
                PropertyName = property.PropertyName,
                Income = property.Income,
                ModeledInterest = property.ModeledInterest,
                Depreciation = property.Depreciation,
                DepreciationIsFirstYearEstimate = property.DepreciationIsFirstYearEstimate,
                TotalExpenses = property.TotalExpenses,
                NetIncome = property.NetIncome,
                Category = (ScheduleECategory?)category.Category,
                CategoryAmount = (decimal?)category.Amount ?? 0m,
                ReportTotalRentalIncome = totals.TotalRentalIncome,
                ReportTotalExpenses = totals.TotalExpenses,
                CanViewUnallocated = totals.CanViewUnallocated,
                UnallocatedIncomeEntryCount = totals.UnallocatedIncomeEntryCount,
                UnallocatedRentalIncome = totals.UnallocatedRentalIncome,
                UnallocatedExpenseCount = totals.UnallocatedExpenseCount,
                UnallocatedTotalExpenses = totals.UnallocatedTotalExpenses,
            };

        var allocatedCategoryFlatRowsQuery =
            from totals in reportTotalsQuery
            from category in allocatedCategoryTotalsQuery
            select new ScheduleEFlatSqlRow
            {
                RowKind = ScheduleEFlatRowKind.AllocatedCategory,
                PropertyId = 0,
                PropertyName = string.Empty,
                Income = 0m,
                ModeledInterest = 0m,
                Depreciation = 0m,
                DepreciationIsFirstYearEstimate = false,
                TotalExpenses = 0m,
                NetIncome = 0m,
                Category = category.Category,
                CategoryAmount = category.Amount,
                ReportTotalRentalIncome = totals.TotalRentalIncome,
                ReportTotalExpenses = totals.TotalExpenses,
                CanViewUnallocated = totals.CanViewUnallocated,
                UnallocatedIncomeEntryCount = totals.UnallocatedIncomeEntryCount,
                UnallocatedRentalIncome = totals.UnallocatedRentalIncome,
                UnallocatedExpenseCount = totals.UnallocatedExpenseCount,
                UnallocatedTotalExpenses = totals.UnallocatedTotalExpenses,
            };

        var unallocatedFlatRowsQuery =
            from totals in reportTotalsQuery
            where totals.CanViewUnallocated
            join category in unallocatedCategoryTotalsQuery
                on 1 equals 1 into categories
            from category in categories.DefaultIfEmpty()
            select new ScheduleEFlatSqlRow
            {
                RowKind = ScheduleEFlatRowKind.UnallocatedCategory,
                PropertyId = 0,
                PropertyName = string.Empty,
                Income = 0m,
                ModeledInterest = 0m,
                Depreciation = 0m,
                DepreciationIsFirstYearEstimate = false,
                TotalExpenses = 0m,
                NetIncome = 0m,
                Category = (ScheduleECategory?)category.Category,
                CategoryAmount = (decimal?)category.Amount ?? 0m,
                ReportTotalRentalIncome = totals.TotalRentalIncome,
                ReportTotalExpenses = totals.TotalExpenses,
                CanViewUnallocated = totals.CanViewUnallocated,
                UnallocatedIncomeEntryCount = totals.UnallocatedIncomeEntryCount,
                UnallocatedRentalIncome = totals.UnallocatedRentalIncome,
                UnallocatedExpenseCount = totals.UnallocatedExpenseCount,
                UnallocatedTotalExpenses = totals.UnallocatedTotalExpenses,
            };

        var flatRows = await propertyFlatRowsQuery
            .Concat(allocatedCategoryFlatRowsQuery)
            .Concat(unallocatedFlatRowsQuery)
            .OrderBy(row => row.RowKind)
            .ThenBy(row => row.PropertyName)
            .ThenBy(row => row.PropertyId)
            .ThenBy(row => row.Category)
            .ToListAsync(ct);

        // SQL already authorized, resolved scope, grouped, aggregated, and ordered the flat rows. This
        // pass only restores nested DTO collections; it never filters or aggregates business values.
        var reports = new List<ScheduleEPropertyReport>();
        var reportCategories = new List<ScheduleECategoryAmount>();
        var unallocatedCategories = new List<ScheduleECategoryAmount>();
        ScheduleEFlatSqlRow? currentProperty = null;
        List<ScheduleECategoryAmount>? currentCategories = null;
        foreach (var row in flatRows)
        {
            if (row.RowKind == ScheduleEFlatRowKind.Property)
            {
                if (currentProperty is null || currentProperty.PropertyId != row.PropertyId)
                {
                    currentProperty = row;
                    currentCategories = [];
                    reports.Add(new ScheduleEPropertyReport(
                        row.PropertyId,
                        row.PropertyName,
                        row.Income,
                        currentCategories,
                        row.TotalExpenses,
                        row.NetIncome,
                        row.ModeledInterest,
                        row.Depreciation,
                        row.DepreciationIsFirstYearEstimate && row.Depreciation > 0m));
                }

                if (row.Category.HasValue)
                {
                    currentCategories!.Add(new ScheduleECategoryAmount(
                        row.Category.Value.ToString(), row.CategoryAmount));
                }
            }
            else if (row.RowKind == ScheduleEFlatRowKind.AllocatedCategory && row.Category.HasValue)
            {
                reportCategories.Add(new ScheduleECategoryAmount(
                    row.Category.Value.ToString(), row.CategoryAmount));
            }
            else if (row.RowKind == ScheduleEFlatRowKind.UnallocatedCategory && row.Category.HasValue)
            {
                unallocatedCategories.Add(new ScheduleECategoryAmount(
                    row.Category.Value.ToString(), row.CategoryAmount));
            }
        }

        var reportTotalRentalIncome = flatRows.Count == 0 ? 0m : flatRows[0].ReportTotalRentalIncome;
        var reportTotalExpenses = flatRows.Count == 0 ? 0m : flatRows[0].ReportTotalExpenses;
        var canViewUnallocated = flatRows.Count != 0 && flatRows[0].CanViewUnallocated;
        var unallocatedIncomeEntryCount = flatRows.Count == 0 ? 0 : flatRows[0].UnallocatedIncomeEntryCount;
        var unallocatedRentalIncome = flatRows.Count == 0 ? 0m : flatRows[0].UnallocatedRentalIncome;
        var unallocatedExpenseCount = flatRows.Count == 0 ? 0 : flatRows[0].UnallocatedExpenseCount;
        var unallocatedTotalExpenses = flatRows.Count == 0 ? 0m : flatRows[0].UnallocatedTotalExpenses;
        var requiresAllocation = unallocatedIncomeEntryCount != 0 || unallocatedExpenseCount != 0;
        var reconciledRentalIncome = reportTotalRentalIncome + unallocatedRentalIncome;
        var reconciledExpenses = reportTotalExpenses + unallocatedTotalExpenses;

        return new ScheduleEReport
        {
            Year = year,
            Properties = reports,
            ExpensesByCategory = reportCategories,
            TotalRentalIncome = reportTotalRentalIncome,
            TotalExpenses = reportTotalExpenses,
            NetIncome = reportTotalRentalIncome - reportTotalExpenses,
            UnallocatedActivity = new ScheduleEUnallocatedActivity
            {
                CanView = canViewUnallocated,
                RequiresAllocation = requiresAllocation,
                IncomeEntryCount = unallocatedIncomeEntryCount,
                RentalIncome = unallocatedRentalIncome,
                ExpenseCount = unallocatedExpenseCount,
                ExpensesByCategory = unallocatedCategories,
                TotalExpenses = unallocatedTotalExpenses,
                NetIncome = unallocatedRentalIncome - unallocatedTotalExpenses,
                Warning = requiresAllocation
                    ? "Some tax activity is not assigned to a property. Allocate it before filing; it is excluded from per-property Schedule E totals."
                    : string.Empty,
            },
            ReconciledTotalRentalIncome = reconciledRentalIncome,
            ReconciledTotalExpenses = reconciledExpenses,
            ReconciledNetIncome = reconciledRentalIncome - reconciledExpenses,
        };
    }

    private IQueryable<Portfolio> WorkspaceAdministratorPortfolio(
        WorkspaceReadScope scope,
        DateTime utcNow) =>
        _db.Portfolios
            .AsNoTracking()
            .Where(portfolio =>
                portfolio.Id == scope.PortfolioId &&
                _db.AuthSessions.AsNoTracking().Any(session =>
                    session.Id == scope.SessionId &&
                    session.UserId == scope.UserId &&
                    session.ActiveAccessContextId == scope.AccessContextId &&
                    session.Status == AuthSessionStatus.Active &&
                    session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > utcNow &&
                    session.ActiveAccessContext != null &&
                    session.ActiveAccessContext.Id == scope.AccessContextId &&
                    session.ActiveAccessContext.UserId == scope.UserId &&
                    session.ActiveAccessContext.PortfolioId == scope.PortfolioId &&
                    session.ActiveAccessContext.AccessRevision == scope.AccessRevision &&
                    session.ActiveAccessContext.Status == WorkspaceAccessContextStatus.Active &&
                    session.ActiveAccessContext.SuspendedAtUtc == null &&
                    session.ActiveAccessContext.RevokedAtUtc == null &&
                    session.ActiveAccessContext.Membership != null &&
                    session.ActiveAccessContext.Membership.PortfolioId == portfolio.Id &&
                    session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active &&
                    session.ActiveAccessContext.Membership.SuspendedAtUtc == null &&
                    session.ActiveAccessContext.Membership.RevokedAtUtc == null &&
                    session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow &&
                    (session.ActiveAccessContext.Membership.EffectiveToUtc == null ||
                     session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow) &&
                    session.ActiveAccessContext.Membership.RoleAssignments.Any(assignment =>
                        assignment.PortfolioId == portfolio.Id &&
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null &&
                        assignment.EffectiveFromUtc <= utcNow &&
                        (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                        assignment.RoleProfile != null &&
                        assignment.RoleProfile.Key == RoleProfileKeys.WorkspaceAdministrator)));

    private enum ScheduleEFlatRowKind
    {
        Property = 0,
        AllocatedCategory = 1,
        UnallocatedCategory = 2,
    }

    private sealed class ScheduleEExpenseFact
    {
        public int? PropertyId { get; set; }
        public ScheduleECategory Category { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class ScheduleEIncomeComponent
    {
        public int? PropertyId { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class ScheduleECategoryComponent
    {
        public int PropertyId { get; set; }
        public ScheduleECategory Category { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class ScheduleECategorySqlRow
    {
        public int PropertyId { get; set; }
        public ScheduleECategory Category { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class ScheduleEReportCategorySqlRow
    {
        public ScheduleECategory Category { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class ScheduleEReportTotalsSqlRow
    {
        public decimal TotalRentalIncome { get; set; }
        public decimal TotalExpenses { get; set; }
        public bool CanViewUnallocated { get; set; }
        public int UnallocatedIncomeEntryCount { get; set; }
        public decimal UnallocatedRentalIncome { get; set; }
        public int UnallocatedExpenseCount { get; set; }
        public decimal UnallocatedTotalExpenses { get; set; }
    }

    private sealed class ScheduleEPropertySqlRow
    {
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public decimal Income { get; set; }
        public decimal ModeledInterest { get; set; }
        public decimal Depreciation { get; set; }
        public bool DepreciationIsFirstYearEstimate { get; set; }
        public decimal TotalExpenses { get; set; }
        public decimal NetIncome { get; set; }
    }

    private sealed class ScheduleEFlatSqlRow
    {
        public ScheduleEFlatRowKind RowKind { get; set; }
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public decimal Income { get; set; }
        public decimal ModeledInterest { get; set; }
        public decimal Depreciation { get; set; }
        public bool DepreciationIsFirstYearEstimate { get; set; }
        public decimal TotalExpenses { get; set; }
        public decimal NetIncome { get; set; }
        public ScheduleECategory? Category { get; set; }
        public decimal CategoryAmount { get; set; }
        public decimal ReportTotalRentalIncome { get; set; }
        public decimal ReportTotalExpenses { get; set; }
        public bool CanViewUnallocated { get; set; }
        public int UnallocatedIncomeEntryCount { get; set; }
        public decimal UnallocatedRentalIncome { get; set; }
        public int UnallocatedExpenseCount { get; set; }
        public decimal UnallocatedTotalExpenses { get; set; }
    }

}
