using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Property"/>. Every query is filtered by the
/// caller's portfolio id (sourced from the JWT claim, never a client parameter). Soft-delete is used
/// for removal. Create/update/delete broadcast realtime updates via <see cref="Core.Interfaces.IDataUpdateService"/>.
/// </summary>
public interface IPropertyService
{
    Task<IReadOnlyList<PropertyResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default);
    Task<PropertyListResponse> ListPageAsync(int portfolioId, PropertyListQuery query, CancellationToken ct = default);
    Task<PropertyResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<PropertyResponse?> CreateAsync(int portfolioId, CreatePropertyRequest request, CancellationToken ct = default);
    Task<PropertyResponse?> UpdateAsync(int portfolioId, int id, UpdatePropertyRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
