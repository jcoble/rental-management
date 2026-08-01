using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface IOwnerContributionService
{
    Task<IReadOnlyList<OwnerContributionResponse>> ListAsync(
        WorkspaceReadScope scope, OwnerContributionListQuery query, CancellationToken ct = default);

    Task<AccountingPage<OwnerContributionResponse>> ListPageAsync(
        WorkspaceReadScope scope, OwnerContributionListQuery query, CancellationToken ct = default);

    Task<OwnerContributionResponse?> GetAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default);

    Task<OwnerContributionResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateOwnerContributionRequest request, string idempotencyKey,
        CancellationToken ct = default);

    Task<OwnerContributionResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateOwnerContributionRequest request, string idempotencyKey,
        CancellationToken ct = default);

    Task<OwnerContributionResponse?> ApproveAsync(
        WorkspaceReadScope scope, int id, ApproveOwnerContributionRequest request, string idempotencyKey,
        CancellationToken ct = default);

    Task<OwnerContributionResponse?> RejectAsync(
        WorkspaceReadScope scope, int id, RejectOwnerContributionRequest request, string idempotencyKey,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default);
}
