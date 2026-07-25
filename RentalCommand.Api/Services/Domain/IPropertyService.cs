using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Property"/>. Every query is filtered by the
/// caller's portfolio id (sourced from the JWT claim, never a client parameter). Property creation
/// and its explicit initial Units use one atomic setup command; removal is a soft-delete.
/// </summary>
public interface IPropertyService
{
    Task<IReadOnlyList<PropertyResponse>> ListAsync(
        WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<PropertyListResponse> ListPageAsync(
        WorkspaceReadScope scope, PropertyListQuery query, CancellationToken ct = default);
    Task<PropertyResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<PropertySetupResponse?> SetupAsync(WorkspaceReadScope scope, SetupPropertyRequest request, string operationKey, CancellationToken ct = default);
    Task<PropertyResponse?> UpdateAsync(WorkspaceReadScope scope, int id, UpdatePropertyRequest request, string operationKey, CancellationToken ct = default);
    Task<bool> DeleteAsync(WorkspaceReadScope scope, int id, string operationKey, CancellationToken ct = default);
}
