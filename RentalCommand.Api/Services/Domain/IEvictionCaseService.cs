using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface IEvictionCaseService
{
    Task<IReadOnlyList<EvictionCaseResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, EvictionCaseListQuery query, CancellationToken ct = default);
    Task<EvictionCaseListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, EvictionCaseListQuery query, CancellationToken ct = default);
    Task<EvictionCaseResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<EvictionCaseResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope, CreateEvictionCaseRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<EvictionCaseResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope, int id, UpdateEvictionCaseRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<EvictionCaseResponse?> AddEventAuthorizedAsync(
        WorkspaceReadScope scope, int id, CreateEvictionCaseEventRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default);

}
