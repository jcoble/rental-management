using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio access for the caller's currently selected canonical workspace context.
/// Reads/updates/deletes are constrained to that server-validated portfolio id; never a client-supplied value.
/// </summary>
public interface IPortfolioService
{
    /// <summary>The portfolio selected by the caller's active workspace context.</summary>
    Task<IReadOnlyList<PortfolioResponse>> ListForUserAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>Get one portfolio, only if it is the caller's own portfolio.</summary>
    Task<PortfolioResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Server-side aggregate facts for the getting-started checklist. Clients use this instead of
    /// downloading properties/tenants/leases/settings to count them locally.
    /// </summary>
    Task<GettingStartedSignalsResponse?> GetGettingStartedSignalsAsync(int portfolioId, CancellationToken ct = default);

    Task<PortfolioResponse?> UpdateAsync(int portfolioId, int id, UpdatePortfolioRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
