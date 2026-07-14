using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped reads and receipt-backed mutations for
/// <see cref="Core.Entities.RecurringMaintenanceTask"/> — the standing chores
/// the Engine turns into work orders each period. Every query is filtered by the caller's portfolio id;
/// inbound property/unit/vendor references are validated in-portfolio. Removal is a soft delete (the
/// global query filter hides it); a toggle endpoint flips the per-task active switch. Mutations stage
/// durable realtime updates in the same atomic attempt.
/// </summary>
public interface IRecurringMaintenanceTaskService
{
    Task<IReadOnlyList<RecurringMaintenanceTaskResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, int? propertyId, bool? activeOnly, ListQuery query,
        CancellationToken ct = default);
    Task<RecurringMaintenanceTaskListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, int? propertyId, bool? activeOnly, ListQuery query,
        CancellationToken ct = default);
    Task<RecurringMaintenanceTaskResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<RecurringMaintenanceTaskResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope, CreateRecurringMaintenanceTaskRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<RecurringMaintenanceTaskResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope, int id, UpdateRecurringMaintenanceTaskRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<RecurringMaintenanceTaskResponse?> SetActiveAuthorizedAsync(
        WorkspaceReadScope scope, int id, bool isActive, string idempotencyKey,
        CancellationToken ct = default);
    Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default);

    Task<IReadOnlyList<RecurringMaintenanceTaskResponse>> ListAsync(int portfolioId, int? propertyId, bool? activeOnly, ListQuery query, CancellationToken ct = default);
    Task<RecurringMaintenanceTaskListResponse> ListPageAsync(int portfolioId, int? propertyId, bool? activeOnly, ListQuery query, CancellationToken ct = default);
    Task<RecurringMaintenanceTaskResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
}
