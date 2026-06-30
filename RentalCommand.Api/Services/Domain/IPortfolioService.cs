using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio access for the current user. A user is scoped to their own portfolio via the JWT
/// <c>portfolioId</c> claim, so "list" returns just that portfolio (Phase 0 is single-portfolio-per-user).
/// Reads/updates/deletes are constrained to the caller's portfolio id; never a client-supplied value.
/// </summary>
public interface IPortfolioService
{
    /// <summary>The caller's portfolio(s) — in Phase 0 this is the single portfolio on their claim.</summary>
    Task<IReadOnlyList<PortfolioResponse>> ListForUserAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>Get one portfolio, only if it is the caller's own portfolio.</summary>
    Task<PortfolioResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Server-side aggregate facts for the getting-started checklist. Clients use this instead of
    /// downloading properties/tenants/leases/settings to count them locally.
    /// </summary>
    Task<GettingStartedSignalsResponse?> GetGettingStartedSignalsAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>Create a new portfolio and scope the current user to it (sets their PortfolioId).</summary>
    Task<PortfolioResponse> CreateAsync(int userId, CreatePortfolioRequest request, CancellationToken ct = default);

    Task<PortfolioResponse?> UpdateAsync(int portfolioId, int id, UpdatePortfolioRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
