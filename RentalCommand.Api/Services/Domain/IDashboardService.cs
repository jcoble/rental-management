using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Aggregates the read-only KPI rollup the web dashboard renders for a single portfolio. Scope is
/// the caller's server-validated canonical access context (never a client-supplied id); returns
/// <c>null</c> when that portfolio does not exist.
/// </summary>
public interface IDashboardService
{
    Task<DashboardResponse?> GetDashboardAsync(WorkspaceReadScope scope, CancellationToken ct = default);
}
