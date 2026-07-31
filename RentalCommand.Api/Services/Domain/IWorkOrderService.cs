using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.WorkOrder"/>. Every query is filtered by the
/// caller's portfolio id. Work orders are not soft-deletable (no DeletedAt column), so removal is a
/// hard delete; mutations broadcast realtime updates.
/// </summary>
public interface IWorkOrderService
{
    Task<IReadOnlyList<WorkOrderResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, int? propertyId, int? unitId, int? vendorId, ListQuery query,
        CancellationToken ct = default);
    Task<WorkOrderListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, WorkOrderListQuery query, CancellationToken ct = default);
    Task<WorkOrderDetailResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<WorkOrderResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope, CreateWorkOrderRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<WorkOrderResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope, int id, UpdateWorkOrderRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<WorkOrderMutationReceipt?> CommentAuthorizedAsync(
        WorkspaceReadScope scope, int id, WorkOrderCommentRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default);

    Task<IReadOnlyList<WorkOrderResponse>> ListAsync(int portfolioId, int? propertyId, int? unitId, int? vendorId, ListQuery query, CancellationToken ct = default);
    Task<WorkOrderListResponse> ListPageAsync(int portfolioId, WorkOrderListQuery query, CancellationToken ct = default);

    /// <summary>
    /// Work-order detail including the status <see cref="WorkOrderDetailResponse.Timeline"/>
    /// (oldest → newest). Returns null when the work order is not in the caller's portfolio.
    /// </summary>
    Task<WorkOrderDetailResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

}
