using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.WorkOrder"/>. Every query is filtered by the
/// caller's portfolio id. Work orders are not soft-deletable (no DeletedAt column), so removal is a
/// hard delete; mutations broadcast realtime updates.
/// </summary>
public interface IWorkOrderService
{
    Task<IReadOnlyList<WorkOrderResponse>> ListAsync(int portfolioId, int? propertyId, int? vendorId, ListQuery query, CancellationToken ct = default);
    Task<WorkOrderResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<WorkOrderResponse?> CreateAsync(int portfolioId, CreateWorkOrderRequest request, CancellationToken ct = default);
    Task<WorkOrderResponse?> UpdateAsync(int portfolioId, int id, UpdateWorkOrderRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
