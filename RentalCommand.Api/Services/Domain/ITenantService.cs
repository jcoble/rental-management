using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Tenant"/>. Every query is filtered by the
/// caller's portfolio id (sourced from the JWT claim, never a client parameter). Soft-delete is used
/// for removal. Create/update/delete broadcast realtime updates via <see cref="Core.Interfaces.IDataUpdateService"/>.
/// </summary>
public interface ITenantService
{
    Task<IReadOnlyList<TenantResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, TenantListQuery query, CancellationToken ct = default);
    Task<TenantListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, TenantListQuery query, CancellationToken ct = default);
    Task<TenantResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<TenantResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope, CreateTenantRequest request, string operationKey, CancellationToken ct = default);
    Task<IReadOnlyList<TenantResponse>> CreateGuidedSetupBatchAsync(
        WorkspaceReadScope scope, GuidedTenantSetupRequest request, string operationKey,
        CancellationToken ct = default);
    Task<TenantResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope, int id, UpdateTenantRequest request, string operationKey, CancellationToken ct = default);
    Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope, int id, string operationKey, CancellationToken ct = default);

}
