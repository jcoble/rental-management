using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.OwnerEntity"/> (Person/LLC/Trust). Every query is
/// filtered by the caller's portfolio id. Soft-delete is used for removal; mutations broadcast updates.
/// </summary>
public interface IOwnerEntityService
{
    Task<IReadOnlyList<OwnerEntityResponse>> ListAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<OwnerEntityListResponse> ListPageAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<OwnerEntityResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<OwnerEntityResponse?> CreateAsync(WorkspaceReadScope scope, CreateOwnerEntityRequest request, string operationKey, CancellationToken ct = default);
    Task<OwnerEntityResponse?> UpdateAsync(WorkspaceReadScope scope, int id, UpdateOwnerEntityRequest request, string operationKey, CancellationToken ct = default);
    Task<ActivateOwnerPortalAccessResponse> ActivateOwnerPortalAccessAsync(
        WorkspaceReadScope scope,
        int id,
        ActivateOwnerPortalAccessRequest request,
        string operationKey,
        CancellationToken ct = default);
    Task<RevokeOwnerPortalAccessResponse> RevokeOwnerPortalAccessAsync(
        WorkspaceReadScope scope,
        int id,
        RevokeOwnerPortalAccessRequest request,
        string operationKey,
        CancellationToken ct = default);
    Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default);
}
