using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

public static class WorkspaceMoneyAuthorizationQuery
{
    public static IQueryable<Expense> WhereAuthorized(
        this IQueryable<Expense> query, RentalCommandDbContext db, WorkspaceReadScope scope,
        string capabilityKey, DateTime utcNow)
    {
        var properties = db.Properties.AsNoTracking().WhereAuthorized(db, scope, capabilityKey, utcNow);
        var unallocated = db.AuthorizedAllPropertyAssignments(
            scope, capabilityKey, CapabilityAuthorizationTargetKind.Property, utcNow);
        return query.Where(expense =>
            expense.PortfolioId == scope.PortfolioId &&
            (properties.Any(property =>
                 property.Id == expense.PropertyId ||
                 (expense.PropertyId == null && expense.UnitId != null &&
                  db.Units.Any(unit => unit.Id == expense.UnitId && unit.PortfolioId == expense.PortfolioId &&
                                       unit.PropertyId == property.Id)) ||
                 (expense.PropertyId == null && expense.UnitId == null && expense.WorkOrderId != null &&
                  db.WorkOrders.Any(workOrder => workOrder.Id == expense.WorkOrderId &&
                                                   workOrder.PortfolioId == expense.PortfolioId &&
                                                   workOrder.PropertyId == property.Id))) ||
             (expense.PropertyId == null && expense.UnitId == null && expense.WorkOrderId == null &&
              unallocated.Any())));
    }

    public static IQueryable<RecurringExpense> WhereAuthorized(
        this IQueryable<RecurringExpense> query, RentalCommandDbContext db, WorkspaceReadScope scope,
        string capabilityKey, DateTime utcNow)
    {
        var properties = db.Properties.AsNoTracking().WhereAuthorized(db, scope, capabilityKey, utcNow);
        var unallocated = db.AuthorizedAllPropertyAssignments(
            scope, capabilityKey, CapabilityAuthorizationTargetKind.Property, utcNow);
        return query.Where(expense =>
            expense.PortfolioId == scope.PortfolioId &&
            (properties.Any(property =>
                 property.Id == expense.PropertyId ||
                 (expense.PropertyId == null && expense.UnitId != null &&
                  db.Units.Any(unit => unit.Id == expense.UnitId && unit.PortfolioId == expense.PortfolioId &&
                                       unit.PropertyId == property.Id))) ||
             (expense.PropertyId == null && expense.UnitId == null && unallocated.Any())));
    }

    public static IQueryable<Loan> WhereAuthorized(
        this IQueryable<Loan> query, RentalCommandDbContext db, WorkspaceReadScope scope,
        string capabilityKey, DateTime utcNow)
    {
        var properties = db.Properties.AsNoTracking().WhereAuthorized(db, scope, capabilityKey, utcNow);
        return query.Where(loan =>
            loan.PortfolioId == scope.PortfolioId && properties.Any(property => property.Id == loan.PropertyId));
    }

    public static IQueryable<OwnerDistribution> WhereAuthorized(
        this IQueryable<OwnerDistribution> query, RentalCommandDbContext db, WorkspaceReadScope scope,
        string capabilityKey, DateTime utcNow)
    {
        var properties = db.Properties.AsNoTracking().WhereAuthorized(db, scope, capabilityKey, utcNow);
        var unallocated = db.AuthorizedAllPropertyAssignments(
            scope, capabilityKey, CapabilityAuthorizationTargetKind.Property, utcNow);
        return query.Where(distribution =>
            distribution.PortfolioId == scope.PortfolioId &&
            ((distribution.PropertyId != null &&
              properties.Any(property => property.Id == distribution.PropertyId)) ||
             (distribution.PropertyId == null && unallocated.Any())));
    }

    public static IQueryable<OwnerContribution> WhereAuthorized(
        this IQueryable<OwnerContribution> query, RentalCommandDbContext db, WorkspaceReadScope scope,
        string capabilityKey, DateTime utcNow)
    {
        var properties = db.Properties.AsNoTracking().WhereAuthorized(db, scope, capabilityKey, utcNow);
        var unallocated = db.AuthorizedAllPropertyAssignments(
            scope, capabilityKey, CapabilityAuthorizationTargetKind.Property, utcNow);
        return query.Where(contribution =>
            contribution.PortfolioId == scope.PortfolioId &&
            ((contribution.PropertyId != null &&
              properties.Any(property => property.Id == contribution.PropertyId)) ||
             (contribution.PropertyId == null && unallocated.Any())));
    }
}
