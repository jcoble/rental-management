using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Expense"/>. Every query is filtered by the caller's
/// portfolio id (sourced from the JWT claim, never a client parameter). Soft-delete is used for removal.
/// Create/update/delete broadcast realtime updates via <see cref="Core.Interfaces.IDataUpdateService"/>.
/// </summary>
public interface IExpenseService
{
    Task<IReadOnlyList<ExpenseResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<ExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<ExpenseResponse> CreateAsync(int portfolioId, CreateExpenseRequest request, CancellationToken ct = default);
    Task<ExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateExpenseRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
