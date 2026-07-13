using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for recurring-expense templates. Inbound property/unit references are
/// validated in-portfolio (IDOR guard). The Engine worker materializes these into expense rows.
/// </summary>
public interface IRecurringExpenseService
{
    Task<IReadOnlyList<RecurringExpenseResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<RecurringExpenseResponse>> ListAsync(WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<RecurringExpenseListResponse> ListPageAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<RecurringExpenseListResponse> ListPageAsync(WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<RecurringExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<RecurringExpenseResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<RecurringExpenseResponse?> CreateAsync(int portfolioId, CreateRecurringExpenseRequest request, CancellationToken ct = default);
    Task<RecurringExpenseResponse?> CreateAsync(WorkspaceReadScope scope, CreateRecurringExpenseRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<RecurringExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateRecurringExpenseRequest request, CancellationToken ct = default);
    Task<RecurringExpenseResponse?> UpdateAsync(WorkspaceReadScope scope, int id, UpdateRecurringExpenseRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<bool> DeleteAsync(WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default);
}
