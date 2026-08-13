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

    Task<OwnerDistributionResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<OwnerDistributionResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);

    Task<OwnerDistributionResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default);

    Task<OwnerDistributionResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default);

    Task<OwnerDistributionResponse?> ApproveAsync(
        WorkspaceReadScope scope, int id, ApproveOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default);

    Task<OwnerDistributionResponse?> RejectAsync(
        WorkspaceReadScope scope, int id, RejectOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default);

    Task<bool> DeleteAsync(WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default);
}
