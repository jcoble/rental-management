using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Reporting;

public sealed class FinancialReportExpenseProjection
{
    public int ExpenseId { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? WorkOrderId { get; set; }
    public ScheduleECategory Category { get; set; }
    public ExpenseStatus Status { get; set; }
    public DateTime EffectiveAt { get; set; }
    public decimal Amount { get; set; }
}

public sealed class FinancialReportIncomeProjection
{
    public int? PropertyId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Amount { get; set; }
}

public sealed class CashFlowMonthProjection
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Income { get; set; }
    public decimal Expense { get; set; }
    public decimal Net { get; set; }
}

public sealed class SecurityDepositRegisterProjection
{
    public int DepositId { get; set; }
    public int LeaseManagementId { get; set; }
    public string RelationshipNumber { get; set; } = string.Empty;
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;
    public string? TenantName { get; set; }
    public decimal Held { get; set; }
    public decimal Deductions { get; set; }
    public decimal Returned { get; set; }
    public decimal CurrentBalance { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime HeldAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
}

public static class FinancialReportProjections
{
    public static IQueryable<FinancialReportIncomeProjection> BuildAuthorizedCashFlowIncomeProjection(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string capabilityKey,
        DateTime utcNow,
        DateTime from,
        DateTime to,
        IReadOnlyCollection<int> propertyIds)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = BuildAuthorizedPropertyQuery(db, scope, capabilityKey, utcNow, propertyIds);
        var fromDate = DateOnly.FromDateTime(from);
        var toDate = DateOnly.FromDateTime(to);

        var tenantIncome =
            from allocation in db.TenantLedgerAllocations.AsNoTracking()
            join receipt in db.TenantLedgerEntries.AsNoTracking()
                on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.CreditEntryId }
                equals new { receipt.PortfolioId, receipt.TenantAccountId, receipt.Id }
            join charge in db.TenantLedgerEntries.AsNoTracking()
                on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.DebitEntryId }
                equals new { charge.PortfolioId, charge.TenantAccountId, charge.Id }
            join account in db.TenantAccounts.AsNoTracking()
                on new { allocation.PortfolioId, Id = allocation.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            where allocation.PortfolioId == portfolioId
                && receipt.EntryType == TenantLedgerEntryType.PaymentReceipt
                && receipt.EffectiveOn >= fromDate
                && receipt.EffectiveOn <= toDate
                && charge.EntryType != TenantLedgerEntryType.DepositCharge
            select new FinancialReportIncomeProjection
            {
                PropertyId = management.PropertyId,
                Year = receipt.EffectiveOn.Year,
                Month = receipt.EffectiveOn.Month,
                Amount = allocation.Amount,
            };

        var applicationIncome = db.ApplicationFinancialEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry =>
                entry.PortfolioId == portfolioId &&
                entry.EffectiveOn >= fromDate &&
                entry.EffectiveOn <= toDate)
            .Select(entry => new FinancialReportIncomeProjection
            {
                PropertyId = entry.PropertyId,
                Year = entry.EffectiveOn.Year,
                Month = entry.EffectiveOn.Month,
                Amount = entry.Direction == ApplicationFinancialDirection.Increase
                    ? entry.Amount
                    : -entry.Amount,
            });

        return tenantIncome
            .Concat(applicationIncome)
            .Where(row => row.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == row.PropertyId.Value));
    }

    public static IQueryable<FinancialReportExpenseProjection> BuildAuthorizedCashFlowExpenseProjection(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string capabilityKey,
        DateTime utcNow,
        DateTime from,
        DateTime to,
        IReadOnlyCollection<int> propertyIds)
    {
        var hasPropertyFilter = propertyIds.Count > 0;
        var authorizedProperties = BuildAuthorizedPropertyQuery(db, scope, capabilityKey, utcNow, propertyIds);
        var allPropertiesAuthority = db.AuthorizedAllPropertyAssignments(
                scope,
                capabilityKey,
                CapabilityAuthorizationTargetKind.Property,
                utcNow)
            .Select(_ => 1);

        return BuildExpenseAllocationProjection(db, scope.PortfolioId)
            .Where(expense => expense.EffectiveAt >= from && expense.EffectiveAt <= to)
            .Where(expense =>
                (expense.PropertyId != null &&
                    authorizedProperties.Any(property => property.Id == expense.PropertyId.Value)) ||
                (expense.PropertyId == null && !hasPropertyFilter && allPropertiesAuthority.Any()));
    }

    public static IQueryable<int> BuildAuthorizedCashFlowAnchor(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string capabilityKey,
        DateTime utcNow,
        IReadOnlyCollection<int> propertyIds)
    {
        var authorizedProperties = BuildAuthorizedPropertyQuery(db, scope, capabilityKey, utcNow, propertyIds);

        return db.Portfolios
            .AsNoTracking()
            .Where(candidate => candidate.Id == scope.PortfolioId && authorizedProperties.Any())
            .Select(_ => 1);
    }

    public static IQueryable<FinancialReportExpenseProjection> BuildExpenseAllocationProjection(
        RentalCommandDbContext db,
        int portfolioId)
    {
        var allocated =
            from allocation in db.ExpenseAllocations.AsNoTracking()
            join expense in db.Expenses.AsNoTracking()
                on new { allocation.PortfolioId, Id = allocation.ExpenseId }
                equals new { expense.PortfolioId, expense.Id }
            join unit in db.Units.AsNoTracking()
                on new { allocation.PortfolioId, Id = allocation.UnitId ?? 0 }
                equals new { unit.PortfolioId, unit.Id } into unitJoin
            from unit in unitJoin.DefaultIfEmpty()
            where allocation.PortfolioId == portfolioId
                && expense.Status == ExpenseStatus.Paid
            select new FinancialReportExpenseProjection
            {
                ExpenseId = expense.Id,
                PortfolioId = expense.PortfolioId,
                PropertyId = allocation.TargetKind == ExpenseAllocationTargetKind.Property
                    ? allocation.PropertyId
                    : allocation.TargetKind == ExpenseAllocationTargetKind.Unit
                        ? (unit == null ? null : unit.PropertyId)
                        : expense.PropertyId,
                UnitId = allocation.UnitId,
                WorkOrderId = expense.WorkOrderId,
                Category = expense.Category,
                Status = expense.Status,
                EffectiveAt = expense.PaidAt ?? expense.IncurredAt,
                Amount = allocation.Amount,
            };

        var unallocated =
            from expense in db.Expenses.AsNoTracking()
            where expense.PortfolioId == portfolioId
                && expense.Status == ExpenseStatus.Paid
                && !db.ExpenseAllocations.Any(allocation =>
                    allocation.PortfolioId == expense.PortfolioId &&
                    allocation.ExpenseId == expense.Id)
            select new FinancialReportExpenseProjection
            {
                ExpenseId = expense.Id,
                PortfolioId = expense.PortfolioId,
                PropertyId = expense.PropertyId,
                UnitId = expense.UnitId,
                WorkOrderId = expense.WorkOrderId,
                Category = expense.Category,
                Status = expense.Status,
                EffectiveAt = expense.PaidAt ?? expense.IncurredAt,
                Amount = expense.Amount,
            };

        return allocated.Concat(unallocated);
    }

    private static IQueryable<Property> BuildAuthorizedPropertyQuery(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string capabilityKey,
        DateTime utcNow,
        IReadOnlyCollection<int> propertyIds)
    {
        var properties = db.Properties
            .AsNoTracking()
            .WhereAuthorized(db, scope, capabilityKey, utcNow);

        return propertyIds.Count == 0
            ? properties
            : properties.Where(property => propertyIds.Contains(property.Id));
    }

    public static IQueryable<CashFlowMonthProjection> BuildCashFlowMonthProjection(
        IQueryable<int> anchor,
        IQueryable<FinancialReportIncomeProjection> incomeQuery,
        IQueryable<FinancialReportExpenseProjection> expenseQuery,
        int startYear,
        int startMonth,
        int monthCount)
    {
        var offsets = BuildOffsetQuery(anchor, monthCount);

        var monthlyTotals = offsets
            .Select(offset => new
            {
                Year = startYear + ((startMonth - 1 + offset) / 12),
                Month = ((startMonth - 1 + offset) % 12) + 1,
            })
            .Select(month => new CashFlowMonthProjection
            {
                Year = month.Year,
                Month = month.Month,
                Income = incomeQuery
                    .Where(income => income.Year == month.Year && income.Month == month.Month)
                    .Sum(income => (decimal?)income.Amount) ?? 0m,
                Expense = expenseQuery
                    .Where(expense => expense.EffectiveAt.Year == month.Year &&
                        expense.EffectiveAt.Month == month.Month)
                    .Sum(expense => (decimal?)expense.Amount) ?? 0m,
            });

        return monthlyTotals
            .Select(month => new CashFlowMonthProjection
            {
                Year = month.Year,
                Month = month.Month,
                Income = month.Income,
                Expense = month.Expense,
                Net = month.Income - month.Expense,
            })
            .OrderBy(month => month.Year)
            .ThenBy(month => month.Month);
    }

    public static IQueryable<SecurityDepositRegisterProjection> BuildSecurityDepositRegisterProjection(
        RentalCommandDbContext db,
        int portfolioId)
    {
        return
            from balance in db.SecurityDepositBalanceProjections.AsNoTracking()
            join account in db.SecurityDepositAccounts.AsNoTracking()
                on new { balance.PortfolioId, Id = balance.SecurityDepositAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in db.LeaseManagements.AsNoTracking()
                on new { balance.PortfolioId, Id = balance.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { balance.PortfolioId, balance.LeaseManagementId }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            where balance.PortfolioId == portfolioId
            select new SecurityDepositRegisterProjection
            {
                DepositId = account.Id,
                LeaseManagementId = management.Id,
                RelationshipNumber = management.RelationshipNumber,
                PropertyId = management.PropertyId,
                PropertyName = management.Property!.Name,
                UnitNumber = management.Unit!.UnitNumber,
                TenantName = lifecycle.CurrentPrimaryTenantName,
                Held = balance.TotalReceived
                    + balance.TotalTransferredIn
                    + (balance.NetAdjustments > 0m ? balance.NetAdjustments : 0m),
                Deductions = balance.TotalDeductions,
                Returned = balance.TotalRefunded + balance.TotalTransferredOut,
                CurrentBalance = balance.HeldBalance,
                Status = balance.DepositStatus,
                HeldAt = db.SecurityDepositEntries
                    .Where(entry => entry.PortfolioId == account.PortfolioId
                        && entry.SecurityDepositAccountId == account.Id
                        && entry.EntryType == SecurityDepositEntryType.Receipt)
                    .Select(entry => (DateTime?)entry.PostedAtUtc)
                    .Min() ?? account.CreatedAtUtc,
                ReturnedAt = db.SecurityDepositEntries
                    .Where(entry => entry.PortfolioId == account.PortfolioId
                        && entry.SecurityDepositAccountId == account.Id
                        && (entry.EntryType == SecurityDepositEntryType.Refund ||
                            entry.EntryType == SecurityDepositEntryType.TransferOut))
                    .Select(entry => (DateTime?)entry.PostedAtUtc)
                    .Max(),
            };
    }

    private static IQueryable<int> BuildOffsetQuery(IQueryable<int> anchor, int max)
    {
        var query = anchor.Select(_ => 0);
        for (var offset = 1; offset < max; offset++)
        {
            var captured = offset;
            query = query.Concat(anchor.Select(_ => captured));
        }

        return query;
    }
}
