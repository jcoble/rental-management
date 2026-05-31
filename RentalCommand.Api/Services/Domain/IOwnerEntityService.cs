using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.OwnerEntity"/> (Person/LLC/Trust). Every query is
/// filtered by the caller's portfolio id. Soft-delete is used for removal; mutations broadcast updates.
/// </summary>
public interface IOwnerEntityService
{
    Task<IReadOnlyList<OwnerEntityResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default);
    Task<OwnerEntityResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<OwnerEntityResponse> CreateAsync(int portfolioId, CreateOwnerEntityRequest request, CancellationToken ct = default);
    Task<OwnerEntityResponse?> UpdateAsync(int portfolioId, int id, UpdateOwnerEntityRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
