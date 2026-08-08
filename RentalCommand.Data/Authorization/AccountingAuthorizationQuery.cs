using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Keeps accounting property authorization correlated with the journal-line query so filtering,
/// aggregation, ordering, and paging remain one SQL pipeline.
/// </summary>
public static class AccountingAuthorizationQuery
{
    public static IQueryable<JournalLine> WhereAccountingAuthorized(
        this IQueryable<JournalLine> query,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string capabilityKey,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);
        var properties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, capabilityKey, utcNow);
        var allProperties = db.AuthorizedAllPropertyAssignments(
            scope,
            capabilityKey,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);

        return query.Where(line =>
            line.JournalEntry!.PortfolioId == scope.PortfolioId &&
            (line.PropertyId != null && properties.Any(property =>
                 property.Id == line.PropertyId && property.PortfolioId == line.JournalEntry.PortfolioId) ||
             line.PropertyId == null && line.UnitId != null && db.Units.Any(unit =>
                 unit.Id == line.UnitId &&
                 unit.PortfolioId == line.JournalEntry.PortfolioId &&
                 properties.Any(property => property.Id == unit.PropertyId)) ||
             line.PropertyId == null && line.UnitId == null && line.TenantAccountId != null &&
             db.TenantAccounts.Any(account =>
                 account.Id == line.TenantAccountId &&
                 account.PortfolioId == line.JournalEntry.PortfolioId &&
                 properties.Any(property => property.Id == account.LeaseManagement!.PropertyId)) ||
             line.PropertyId == null && line.UnitId == null && line.TenantAccountId == null &&
             allProperties.Any(assignment => assignment.PortfolioId == line.JournalEntry.PortfolioId)));
    }

    public static Task<bool> CanReadTenantAccountAsync(
        this RentalCommandDbContext db,
        WorkspaceReadScope scope,
        int tenantAccountId,
        string capabilityKey,
        DateTime utcNow,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);
        var properties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, capabilityKey, utcNow);
        return db.TenantAccounts.AsNoTracking().AnyAsync(account =>
            account.Id == tenantAccountId &&
            account.PortfolioId == scope.PortfolioId &&
            properties.Any(property =>
                property.Id == account.LeaseManagement!.PropertyId &&
                property.PortfolioId == account.PortfolioId), ct);
    }
}
