using Microsoft.EntityFrameworkCore;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Cross-tenant guards: confirm an inbound foreign-key reference both exists and belongs to the
/// caller's portfolio before a service assigns it. Centralizing the checks keeps every Create/Update
/// path consistent and prevents IDOR (a caller linking another portfolio's rows into their own).
/// Each helper returns <c>true</c> when the reference is in scope (or not supplied) and <c>false</c>
/// when it is missing or out of scope, so callers can short-circuit with the existing null-return pattern.
/// </summary>
internal static class PortfolioScopeGuards
{
    /// <summary>The referenced property must live in the caller's portfolio.</summary>
    public static Task<bool> EnsurePropertyInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int propertyId, CancellationToken ct)
        => db.Properties.AnyAsync(p => p.Id == propertyId && p.PortfolioId == portfolioId, ct);

    /// <summary>
    /// The referenced unit must belong to a property in the caller's portfolio.
    /// Units have no PortfolioId of their own, so scope is enforced through the owning property.
    /// When <paramref name="propertyId"/> is supplied the unit must additionally belong to that property.
    /// </summary>
    public static Task<bool> EnsureUnitInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int unitId, int? propertyId, CancellationToken ct)
        => propertyId.HasValue
            ? db.Units.AnyAsync(u => u.Id == unitId && u.PropertyId == propertyId.Value &&
                db.Properties.Any(p => p.Id == u.PropertyId && p.PortfolioId == portfolioId), ct)
            : db.Units.AnyAsync(u => u.Id == unitId &&
                db.Properties.Any(p => p.Id == u.PropertyId && p.PortfolioId == portfolioId), ct);

    /// <summary>The referenced tenant must live in the caller's portfolio.</summary>
    public static Task<bool> EnsureTenantInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int tenantId, CancellationToken ct)
        => db.Tenants.AnyAsync(t => t.Id == tenantId && t.PortfolioId == portfolioId, ct);

    public static Task<bool> EnsureLeaseManagementInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int leaseManagementId, CancellationToken ct)
        => db.LeaseManagements.AnyAsync(l => l.Id == leaseManagementId && l.PortfolioId == portfolioId, ct);

    public static Task<bool> EnsureLeaseAgreementInManagementAsync(this RentalCommandDbContext db, int portfolioId, int leaseManagementId, int leaseAgreementId, CancellationToken ct)
        => db.LeaseAgreements.AnyAsync(a => a.Id == leaseAgreementId && a.PortfolioId == portfolioId && a.LeaseManagementId == leaseManagementId, ct);

    /// <summary>The referenced rental application must live in the caller's portfolio.</summary>
    public static Task<bool> EnsureApplicationInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int applicationId, CancellationToken ct)
        => db.RentalApplications.AnyAsync(a => a.Id == applicationId && a.PortfolioId == portfolioId, ct);

    /// <summary>The referenced vendor must live in the caller's portfolio.</summary>
    public static Task<bool> EnsureVendorInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int vendorId, CancellationToken ct)
        => db.Vendors.AnyAsync(v => v.Id == vendorId && v.PortfolioId == portfolioId, ct);

    /// <summary>The referenced work order must live in the caller's portfolio.</summary>
    public static Task<bool> EnsureWorkOrderInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int workOrderId, CancellationToken ct)
        => db.WorkOrders.AnyAsync(w => w.Id == workOrderId && w.PortfolioId == portfolioId, ct);

    /// <summary>The referenced owner (legacy contact) must live in the caller's portfolio.</summary>
    public static Task<bool> EnsureOwnerInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int ownerId, CancellationToken ct)
        => db.Owners.AnyAsync(o => o.Id == ownerId && o.PortfolioId == portfolioId, ct);

    /// <summary>The referenced owner entity (legal owner) must live in the caller's portfolio.</summary>
    public static Task<bool> EnsureOwnerEntityInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int ownerEntityId, CancellationToken ct)
        => db.OwnerEntities.AnyAsync(e => e.Id == ownerEntityId && e.PortfolioId == portfolioId, ct);

    /// <summary>The referenced capital asset must live in the caller's portfolio.</summary>
    public static Task<bool> EnsureCapitalAssetInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int capitalAssetId, CancellationToken ct)
        => db.CapitalAssets.AnyAsync(a => a.Id == capitalAssetId && a.PortfolioId == portfolioId, ct);
}
