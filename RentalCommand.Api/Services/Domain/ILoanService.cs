using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for per-property loans (mortgages) plus read access to a loan's generated
/// amortization schedule. Every inbound property reference is validated in-portfolio (IDOR guard).
/// Mutations are receipt-backed atomic commands with realtime delivery staged in the same transaction.
/// </summary>
public interface ILoanService
{
    Task<IReadOnlyList<LoanResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<LoanResponse>> ListAsync(WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<LoanListResponse> ListPageAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<LoanListResponse> ListPageAsync(WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<LoanResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<LoanResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<LoanResponse?> CreateAsync(WorkspaceReadScope scope, CreateLoanRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<LoanResponse?> UpdateAsync(WorkspaceReadScope scope, int id, UpdateLoanRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<bool> DeleteAsync(WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default);

    /// <summary>The loan's amortization rows (oldest first), or null when the loan is out of scope.</summary>
    Task<IReadOnlyList<LoanPaymentResponse>?> GetPaymentsAsync(int portfolioId, int loanId, CancellationToken ct = default);
    Task<IReadOnlyList<LoanPaymentResponse>?> GetPaymentsAsync(WorkspaceReadScope scope, int loanId, CancellationToken ct = default);
}
