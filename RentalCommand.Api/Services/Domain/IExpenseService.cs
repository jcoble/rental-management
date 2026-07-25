using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Expense"/>. Every query is filtered by the caller's
/// portfolio id (sourced from the JWT claim, never a client parameter). Soft-delete is used for removal.
/// Create/update/delete are receipt-backed atomic commands that stage realtime delivery in the
/// same database transaction as the business mutation.
/// </summary>
public interface IExpenseService
{
    /// <summary>
    /// Lists portfolio expenses, optionally filtered. When <paramref name="unitId"/> is supplied the
    /// result is the unit's own expenses (<c>Expense.UnitId == unitId</c>) plus expenses linked to that
    /// unit's work orders — computed DB-side via a single correlated query (no per-row follow-ups).
    /// </summary>
    Task<IReadOnlyList<ExpenseResponse>> ListAsync(int portfolioId, int? propertyId, int? unitId, int? workOrderId, ListQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseResponse>> ListAsync(WorkspaceReadScope scope, int? propertyId, int? unitId, int? workOrderId, ListQuery query, CancellationToken ct = default);
    Task<ExpenseListResponse> ListPageAsync(
        int portfolioId,
        int? propertyId,
        int? unitId,
        int? workOrderId,
        bool workOrderLinkedOnly,
        ListQuery query,
        CancellationToken ct = default);
    Task<ExpenseListResponse> ListPageAsync(
        WorkspaceReadScope scope, int? propertyId, int? unitId, int? workOrderId,
        bool workOrderLinkedOnly, ListQuery query, CancellationToken ct = default);
    Task<ExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<ExpenseResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<ExpenseResponse?> CreateAsync(WorkspaceReadScope scope, CreateExpenseRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<ExpenseResponse?> UpdateAsync(WorkspaceReadScope scope, int id, UpdateExpenseRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<bool> DeleteAsync(WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default);
}
