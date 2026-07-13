using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Property"/>. Every query is filtered by the
/// caller's portfolio id (sourced from the JWT claim, never a client parameter). Soft-delete is used
/// for removal. Create/update/delete broadcast realtime updates via <see cref="Core.Interfaces.IDataUpdateService"/>.
/// </summary>
public interface IPropertyService
{
    Task<IReadOnlyList<PropertyResponse>> ListAsync(
        WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<PropertyListResponse> ListPageAsync(
        WorkspaceReadScope scope, PropertyListQuery query, CancellationToken ct = default);
    Task<PropertyResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<PropertyResponse?> CreateAsync(WorkspaceReadScope scope, CreatePropertyRequest request, CancellationToken ct = default);
    Task<PropertyResponse?> UpdateAsync(WorkspaceReadScope scope, int id, UpdatePropertyRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
}
