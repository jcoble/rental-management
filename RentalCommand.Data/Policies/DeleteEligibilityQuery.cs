using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Policies;

public static class DeleteEligibilityQuery
{
    public static IQueryable<PropertyDeleteEligibility> PropertyDeleteEligibility(
        this RentalCommandDbContext db,
        int portfolioId,
        int propertyId) =>
        db.Set<Property>().IgnoreQueryFilters().AsNoTracking()
            .Where(property => property.PortfolioId == portfolioId && property.Id == propertyId)
            .Select(property => new PropertyDeleteEligibility
            {
                HasOccupiedUnit = db.Set<UnitOccupancyProjection>().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id && row.IsOccupied),
                HasPlannedOrCurrentRelationship = db.Set<LeaseManagementLifecycleProjection>().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id
                    && row.Lifecycle != "Canceled" && row.Lifecycle != "Closed"
                    && row.Lifecycle != "AccountingCloseout"),
                HasRentalRelationshipHistory = db.Set<LeaseManagement>().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                HasWorkOrderHistory = db.Set<WorkOrder>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                HasAppointmentHistory = db.Set<Appointment>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                HasInspectionHistory = db.Set<Inspection>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                HasExpenseHistory = db.Set<Expense>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                HasApplicationHistory = db.Set<RentalApplication>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                HasRecurringExpenseHistory = db.Set<RecurringExpense>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                HasLoanHistory = db.Set<Loan>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                HasDocumentHistory = db.Set<StoredFile>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.EntityType == nameof(Property)
                    && row.EntityId == property.Id),
            });

    public static IQueryable<UnitDeleteEligibility> UnitDeleteEligibility(
        this RentalCommandDbContext db,
        int portfolioId,
        int unitId) =>
        db.Set<Unit>().IgnoreQueryFilters().AsNoTracking()
            .Where(unit => unit.PortfolioId == portfolioId && unit.Id == unitId)
            .Select(unit => new UnitDeleteEligibility
            {
                IsOccupied = db.Set<UnitOccupancyProjection>().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id && row.IsOccupied),
                HasPlannedOrCurrentRelationship = db.Set<LeaseManagementLifecycleProjection>().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id
                    && row.Lifecycle != "Canceled" && row.Lifecycle != "Closed"
                    && row.Lifecycle != "AccountingCloseout"),
                HasRentalRelationshipHistory = db.Set<LeaseManagement>().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasWorkOrderHistory = db.Set<WorkOrder>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasAppointmentHistory = db.Set<Appointment>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasInspectionHistory = db.Set<Inspection>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasExpenseHistory = db.Set<Expense>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasApplicationHistory = db.Set<RentalApplication>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasRecurringExpenseHistory = db.Set<RecurringExpense>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasDocumentHistory = db.Set<StoredFile>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.EntityType == nameof(Unit)
                    && row.EntityId == unit.Id),
            });

    public static IQueryable<TenantDeleteEligibility> TenantDeleteEligibility(
        this RentalCommandDbContext db,
        int portfolioId,
        int tenantId) =>
        db.Set<Tenant>().AsNoTracking()
            .Where(tenant => tenant.PortfolioId == portfolioId && tenant.Id == tenantId)
            .WithTenantDeleteEligibility(db, portfolioId)
            .Select(row => new TenantDeleteEligibility
            {
                ActiveLeaseCount = row.ActiveLeaseCount,
                LeaseHistoryCount = row.LeaseHistoryCount,
            });

    public static IQueryable<TenantDeleteEligibilityProjection> WithTenantDeleteEligibility(
        this IQueryable<Tenant> tenants,
        RentalCommandDbContext db,
        int portfolioId) =>
        tenants
            .Select(tenant => new TenantDeleteEligibilityProjection
            {
                Entity = tenant,
                ActiveLeaseCount = db.Set<LeaseManagementParty>()
                    .Where(party => party.PortfolioId == portfolioId
                        && party.TenantId == tenant.Id
                        && party.Role != LeaseManagementPartyRole.Guarantor
                        && db.Set<UnitOccupancyProjection>().Any(occupancy =>
                            occupancy.PortfolioId == portfolioId
                            && occupancy.CurrentLeaseManagementId == party.LeaseManagementId)
                        && db.Set<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                            lifecycle.PortfolioId == portfolioId
                            && lifecycle.LeaseManagementId == party.LeaseManagementId
                            && party.EffectiveFrom <= lifecycle.BusinessDate
                            && (party.EffectiveThrough == null
                                || party.EffectiveThrough >= lifecycle.BusinessDate)))
                    .Select(party => party.LeaseManagementId).Distinct().Count(),
                LeaseHistoryCount = db.Set<LeaseManagementParty>()
                    .Where(party => party.PortfolioId == portfolioId && party.TenantId == tenant.Id)
                    .Select(party => party.LeaseManagementId).Distinct().Count(),
            });
}

public sealed class PropertyDeleteEligibility
{
    public bool HasOccupiedUnit { get; init; }
    public bool HasPlannedOrCurrentRelationship { get; init; }
    public bool HasRentalRelationshipHistory { get; init; }
    public bool HasWorkOrderHistory { get; init; }
    public bool HasAppointmentHistory { get; init; }
    public bool HasInspectionHistory { get; init; }
    public bool HasExpenseHistory { get; init; }
    public bool HasApplicationHistory { get; init; }
    public bool HasRecurringExpenseHistory { get; init; }
    public bool HasLoanHistory { get; init; }
    public bool HasDocumentHistory { get; init; }
}

public sealed class UnitDeleteEligibility
{
    public bool IsOccupied { get; init; }
    public bool HasPlannedOrCurrentRelationship { get; init; }
    public bool HasRentalRelationshipHistory { get; init; }
    public bool HasWorkOrderHistory { get; init; }
    public bool HasAppointmentHistory { get; init; }
    public bool HasInspectionHistory { get; init; }
    public bool HasExpenseHistory { get; init; }
    public bool HasApplicationHistory { get; init; }
    public bool HasRecurringExpenseHistory { get; init; }
    public bool HasDocumentHistory { get; init; }
}

public sealed class TenantDeleteEligibility
{
    public int ActiveLeaseCount { get; init; }
    public int LeaseHistoryCount { get; init; }
}

public sealed class TenantDeleteEligibilityProjection
{
    public required Tenant Entity { get; init; }
    public int ActiveLeaseCount { get; init; }
    public int LeaseHistoryCount { get; init; }
}
