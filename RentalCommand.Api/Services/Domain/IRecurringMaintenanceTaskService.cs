using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.RecurringMaintenanceTask"/> — the standing chores
/// the Engine turns into work orders each period. Every query is filtered by the caller's portfolio id;
/// inbound property/unit/vendor references are validated in-portfolio. Removal is a soft delete (the
/// global query filter hides it); a toggle endpoint flips the per-task active switch. Mutations broadcast
/// realtime updates.
/// </summary>
public interface IRecurringMaintenanceTaskService
{
    Task<IReadOnlyList<RecurringMaintenanceTaskResponse>> ListAsync(int portfolioId, int? propertyId, bool? activeOnly, ListQuery query, CancellationToken ct = default);
    Task<RecurringMaintenanceTaskListResponse> ListPageAsync(int portfolioId, int? propertyId, bool? activeOnly, ListQuery query, CancellationToken ct = default);
    Task<RecurringMaintenanceTaskResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<RecurringMaintenanceTaskResponse?> CreateAsync(int portfolioId, CreateRecurringMaintenanceTaskRequest request, CancellationToken ct = default);
    Task<RecurringMaintenanceTaskResponse?> UpdateAsync(int portfolioId, int id, UpdateRecurringMaintenanceTaskRequest request, CancellationToken ct = default);
    Task<RecurringMaintenanceTaskResponse?> SetActiveAsync(int portfolioId, int id, bool isActive, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
