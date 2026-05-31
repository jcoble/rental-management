using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Vendor"/>. Every query is filtered by the caller's
/// portfolio id. Soft-delete is used for removal; mutations broadcast realtime updates.
/// </summary>
public interface IVendorService
{
    Task<IReadOnlyList<VendorResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default);
    Task<VendorResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<VendorResponse> CreateAsync(int portfolioId, CreateVendorRequest request, CancellationToken ct = default);
    Task<VendorResponse?> UpdateAsync(int portfolioId, int id, UpdateVendorRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
