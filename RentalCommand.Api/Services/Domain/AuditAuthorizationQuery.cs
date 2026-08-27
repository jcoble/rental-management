using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Canonical property authorization for user-facing audit/activity reads. The supported entity path,
/// current session/context/revision, <c>reports.read</c> capability, and selected-property scope remain
/// correlated inside the caller's SQL statement; unsupported or workspace-global audit rows fail closed.
/// </summary>
internal static class AuditAuthorizationQuery
{
    internal static IQueryable<AtomicAuditLog> WhereAuthorized(
        this IQueryable<AtomicAuditLog> audits,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        DateTime utcNow)
    {
        var authorizedProperties = db.Properties
            .AsNoTracking()
            .WhereAuthorized(db, scope, CapabilityKeys.ReportsRead, utcNow);
        var allPropertiesAssignments = db.AuthorizedAllPropertyAssignments(
            scope,
            CapabilityKeys.ReportsRead,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);

        return audits.Where(audit =>
            audit.PortfolioId == scope.PortfolioId &&
            ((audit.EntityType == nameof(Property) && db.Properties.Any(property =>
                  property.PortfolioId == scope.PortfolioId &&
                  property.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized => authorized.Id == property.Id))) ||
             (audit.EntityType == nameof(OwnerEntity) && db.OwnerEntities.Any(owner =>
                  owner.PortfolioId == scope.PortfolioId &&
                  owner.Id == audit.EntityId &&
                  owner.DeletedAt == null &&
                  (allPropertiesAssignments.Any() ||
                   db.PropertyOwnerships.Any(ownership =>
                       ownership.PortfolioId == scope.PortfolioId &&
                       ownership.OwnerEntityId == owner.Id &&
                       ownership.EffectiveFromUtc <= utcNow &&
                       (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > utcNow) &&
                       authorizedProperties.Any(authorized =>
                           authorized.Id == ownership.PropertyId))))) ||
             (audit.EntityType == nameof(Unit) && db.Units.Any(unit =>
                  unit.PortfolioId == scope.PortfolioId &&
                  unit.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized => authorized.Id == unit.PropertyId))) ||
             (audit.EntityType == nameof(Tenant) && db.Tenants.Any(tenant =>
                  tenant.PortfolioId == scope.PortfolioId &&
                  tenant.Id == audit.EntityId &&
                  (allPropertiesAssignments.Any() ||
                   db.LeaseManagementParties.Any(party =>
                       party.PortfolioId == scope.PortfolioId &&
                       party.TenantId == tenant.Id &&
                       authorizedProperties.Any(authorized =>
                           authorized.Id == party.LeaseManagement!.PropertyId))))) ||
             (audit.EntityType == nameof(LeaseManagement) && db.LeaseManagements.Any(management =>
                  management.PortfolioId == scope.PortfolioId &&
                  management.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized => authorized.Id == management.PropertyId))) ||
             (audit.EntityType == nameof(LeaseAgreement) && db.LeaseAgreements.Any(agreement =>
                  agreement.PortfolioId == scope.PortfolioId &&
                  agreement.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized =>
                      authorized.Id == agreement.LeaseManagement!.PropertyId))) ||
             (audit.EntityType == nameof(LeaseAddendum) && db.LeaseAddenda.Any(addendum =>
                  addendum.PortfolioId == scope.PortfolioId &&
                  addendum.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized =>
                      authorized.Id == addendum.LeaseManagement!.PropertyId))) ||
             (audit.EntityType == nameof(TenantAccount) && db.TenantAccounts.Any(account =>
                  account.PortfolioId == scope.PortfolioId &&
                  account.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized =>
                      authorized.Id == account.LeaseManagement!.PropertyId))) ||
             (audit.EntityType == nameof(TenantLedgerEntry) && db.TenantLedgerEntries.Any(entry =>
                  entry.PortfolioId == scope.PortfolioId &&
                  entry.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized =>
                      authorized.Id == entry.TenantAccount!.LeaseManagement!.PropertyId))) ||
             (audit.EntityType == nameof(SecurityDepositAccount) && db.SecurityDepositAccounts.Any(deposit =>
                  deposit.PortfolioId == scope.PortfolioId &&
                  deposit.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized =>
                      authorized.Id == deposit.TenantAccount!.LeaseManagement!.PropertyId))) ||
             (audit.EntityType == nameof(SecurityDepositEntry) && db.SecurityDepositEntries.Any(entry =>
                  entry.PortfolioId == scope.PortfolioId &&
                  entry.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized =>
                      authorized.Id == entry.SecurityDepositAccount!.TenantAccount!.LeaseManagement!.PropertyId))) ||
             (audit.EntityType == "SecurityDeposit" && db.SecurityDepositAccounts.Any(deposit =>
                  deposit.PortfolioId == scope.PortfolioId &&
                  deposit.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized =>
                      authorized.Id == deposit.TenantAccount!.LeaseManagement!.PropertyId))) ||
             (audit.EntityType == nameof(WorkOrder) && db.WorkOrders.Any(workOrder =>
                  workOrder.PortfolioId == scope.PortfolioId &&
                  workOrder.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized => authorized.Id == workOrder.PropertyId))) ||
             (audit.EntityType == nameof(Expense) && db.Expenses.Any(expense =>
                  expense.PortfolioId == scope.PortfolioId &&
                  expense.Id == audit.EntityId &&
                  ((expense.PropertyId != null && authorizedProperties.Any(authorized =>
                       authorized.Id == expense.PropertyId.Value)) ||
                   (expense.PropertyId == null && expense.WorkOrderId != null && db.WorkOrders.Any(workOrder =>
                       workOrder.PortfolioId == scope.PortfolioId &&
                       workOrder.Id == expense.WorkOrderId.Value &&
                       authorizedProperties.Any(authorized => authorized.Id == workOrder.PropertyId)))))) ||
             (audit.EntityType == nameof(Appointment) && db.Appointments.Any(appointment =>
                  appointment.PortfolioId == scope.PortfolioId &&
                  appointment.Id == audit.EntityId &&
                  appointment.PropertyId != null &&
                  authorizedProperties.Any(authorized => authorized.Id == appointment.PropertyId.Value))) ||
             (audit.EntityType == nameof(Inspection) && db.Inspections.Any(inspection =>
                  inspection.PortfolioId == scope.PortfolioId &&
                  inspection.Id == audit.EntityId &&
                  authorizedProperties.Any(authorized => authorized.Id == inspection.PropertyId))) ||
             (audit.EntityType == nameof(RentalApplication) && db.RentalApplications.Any(application =>
                  application.PortfolioId == scope.PortfolioId &&
                  application.Id == audit.EntityId &&
                  application.PropertyId != null &&
                  authorizedProperties.Any(authorized => authorized.Id == application.PropertyId.Value)))));
    }
}
