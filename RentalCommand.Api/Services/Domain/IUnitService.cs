using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Unit"/>. Units have no direct PortfolioId; scoping
/// is enforced through the owning <see cref="Core.Entities.Property"/> (its PortfolioId must match the
/// caller's claim). Soft-delete is used for removal; mutations broadcast realtime updates.
/// </summary>
public interface IUnitService
{
    Task<IReadOnlyList<UnitResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<UnitResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>Returns null when the target property is missing or outside the caller's portfolio.</summary>
    Task<UnitResponse?> CreateAsync(int portfolioId, CreateUnitRequest request, CancellationToken ct = default);
    Task<UnitResponse?> UpdateAsync(int portfolioId, int id, UpdateUnitRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
