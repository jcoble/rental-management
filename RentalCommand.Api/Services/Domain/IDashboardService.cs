using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Aggregates the read-only KPI rollup the web dashboard renders for a single portfolio. Scope is
/// the caller's <c>portfolioId</c> claim (never a client-supplied id); returns <c>null</c> when that
/// portfolio does not exist.
/// </summary>
public interface IDashboardService
{
    Task<DashboardResponse?> GetDashboardAsync(int portfolioId, CancellationToken ct = default);
}
