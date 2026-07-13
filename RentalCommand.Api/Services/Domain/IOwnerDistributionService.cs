using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface IOwnerDistributionService
{
    Task<IReadOnlyList<OwnerDistributionResponse>> ListAsync(
        int portfolioId, OwnerDistributionListQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<OwnerDistributionResponse>> ListAsync(
        WorkspaceReadScope scope, OwnerDistributionListQuery query, CancellationToken ct = default);

    Task<OwnerDistributionListResponse> ListPageAsync(
        int portfolioId, OwnerDistributionListQuery query, CancellationToken ct = default);
    Task<OwnerDistributionListResponse> ListPageAsync(
        WorkspaceReadScope scope, OwnerDistributionListQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<OwnerDistributionResponse>> ListForOwnerYearAsync(
        int portfolioId, int ownerEntityId, int year, CancellationToken ct = default);

    Task<decimal> SumForOwnerYearAsync(
        int portfolioId, int ownerEntityId, int year, CancellationToken ct = default);

    Task<OwnerDistributionResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<OwnerDistributionResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);

    Task<OwnerDistributionResponse?> CreateAsync(
        int portfolioId, CreateOwnerDistributionRequest request, CancellationToken ct = default);
    Task<OwnerDistributionResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default);

    Task<OwnerDistributionResponse?> UpdateAsync(
        int portfolioId, int id, UpdateOwnerDistributionRequest request, CancellationToken ct = default);
    Task<OwnerDistributionResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default);

    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<bool> DeleteAsync(WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default);
}
