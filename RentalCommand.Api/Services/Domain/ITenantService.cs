using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Tenant"/>. Every query is filtered by the
/// caller's portfolio id (sourced from the JWT claim, never a client parameter). Soft-delete is used
/// for removal. Create/update/delete broadcast realtime updates via <see cref="Core.Interfaces.IDataUpdateService"/>.
/// </summary>
public interface ITenantService
{
    Task<IReadOnlyList<TenantResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default);
    Task<TenantListResponse> ListPageAsync(int portfolioId, TenantListQuery query, CancellationToken ct = default);
    Task<TenantResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<TenantResponse> CreateAsync(int portfolioId, CreateTenantRequest request, CancellationToken ct = default);
    Task<TenantResponse?> UpdateAsync(int portfolioId, int id, UpdateTenantRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
