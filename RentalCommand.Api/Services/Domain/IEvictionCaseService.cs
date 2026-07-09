using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface IEvictionCaseService
{
    Task<IReadOnlyList<EvictionCaseResponse>> ListAsync(
        int portfolioId, EvictionCaseListQuery query, CancellationToken ct = default);

    Task<EvictionCaseListResponse> ListPageAsync(
        int portfolioId, EvictionCaseListQuery query, CancellationToken ct = default);

    Task<EvictionCaseResponse?> GetAsync(
        int portfolioId, int id, CancellationToken ct = default);

    Task<EvictionCaseResponse?> CreateAsync(
        int portfolioId, CreateEvictionCaseRequest request, CancellationToken ct = default);

    Task<EvictionCaseResponse?> UpdateAsync(
        int portfolioId, int id, UpdateEvictionCaseRequest request, CancellationToken ct = default);

    Task<EvictionCaseResponse?> AddEventAsync(
        int portfolioId, int id, CreateEvictionCaseEventRequest request, CancellationToken ct = default);

    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
