using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Read-only KPI aggregate for the portfolio analytics overview page.
/// Scope is the caller's server-validated canonical access context — never a client-supplied value.
/// </summary>
public interface IAnalyticsService
{
    Task<AnalyticsOverview> GetOverviewAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default);
}
