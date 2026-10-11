using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Assembles the Unit Command Center aggregate (spec section 8) and the per-unit timeline (section 11).
/// Every figure is computed DB-side (EF-translated SQL) — a handful of set-based queries, no N+1, no
/// in-memory grouping. The unit is portfolio-scoped through its owning property; callers pass the JWT
/// portfolio id and the method returns null when the unit is not in that portfolio (404 at the edge).
/// </summary>
public interface IUnitDashboardService
{
    /// <summary>The at-a-glance aggregate for <c>GET /units/{id}/dashboard</c>; null when out of scope.</summary>
    Task<UnitDashboardResponse?> GetDashboardAsync(int portfolioId, int unitId, CancellationToken ct = default);

    /// <summary>
    /// The unit's history as a bounded <see cref="Core.Entities.AtomicAuditLog"/> union over the unit and its
    /// children (lease/payment/work-order/inspection/appointment/expense ids), newest first, paged.
    /// A fixed, small number of queries regardless of data size. Returns an empty list when out of scope.
    /// </summary>
    Task<IReadOnlyList<AuditEntryResponse>> GetTimelineAsync(int portfolioId, int unitId, int skip, int take, CancellationToken ct = default);
}
