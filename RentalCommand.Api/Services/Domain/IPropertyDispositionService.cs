using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface IPropertyDispositionService
{
    Task<IReadOnlyList<PropertyDispositionResponse>> ListAsync(
        int portfolioId, PropertyDispositionListQuery query, CancellationToken ct = default);

    Task<PropertyDispositionListResponse> ListPageAsync(
        int portfolioId, PropertyDispositionListQuery query, CancellationToken ct = default);

    Task<PropertyDispositionResponse?> GetAsync(
        int portfolioId, int id, CancellationToken ct = default);

    Task<PropertyDispositionResponse?> CreateAsync(
        ActiveAccessContext accessContext, CreatePropertyDispositionRequest request,
        string operationKey,
        CancellationToken ct = default);

    Task<PropertyDispositionResponse?> UpdateAsync(
        int portfolioId, int id, UpdatePropertyDispositionRequest request, CancellationToken ct = default);

    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
