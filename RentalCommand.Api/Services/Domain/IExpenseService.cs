using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Expense"/>. Every query is filtered by the caller's
/// portfolio id (sourced from the JWT claim, never a client parameter). Soft-delete is used for removal.
/// Create/update/delete broadcast realtime updates via <see cref="Core.Interfaces.IDataUpdateService"/>.
/// </summary>
public interface IExpenseService
{
    /// <summary>
    /// Lists portfolio expenses, optionally filtered. When <paramref name="unitId"/> is supplied the
    /// result is the unit's own expenses (<c>Expense.UnitId == unitId</c>) plus expenses linked to that
    /// unit's work orders — computed DB-side via a single correlated query (no per-row follow-ups).
    /// </summary>
    Task<IReadOnlyList<ExpenseResponse>> ListAsync(int portfolioId, int? propertyId, int? unitId, int? workOrderId, ListQuery query, CancellationToken ct = default);
    Task<ExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<ExpenseResponse?> CreateAsync(int portfolioId, CreateExpenseRequest request, CancellationToken ct = default);
    Task<ExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateExpenseRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
