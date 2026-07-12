using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Payment"/> plus the mark-paid action. Every query is
/// filtered by the caller's portfolio id (sourced from the JWT claim, never a client parameter). Create
/// verifies the referenced lease belongs to the portfolio; <see cref="CreateAsync"/> returns null when the
/// lease is out of scope. Payment has no soft-delete column, so removal is a hard delete.
/// Create/update/mark-paid/delete broadcast realtime updates via <see cref="Core.Interfaces.IDataUpdateService"/>.
/// </summary>
public interface IPaymentService
{
    Task<IReadOnlyList<PaymentReceiptResponse>> ListAsync(int portfolioId, PaymentListQuery query, CancellationToken ct = default);
    Task<PaymentListResponse> ListPageAsync(int portfolioId, PaymentListQuery query, CancellationToken ct = default);
    Task<PaymentReceiptResponse?> GetAsync(int portfolioId, long id, CancellationToken ct = default);
    Task<PaymentResponse?> CreateAsync(int portfolioId, CreatePaymentRequest request, CancellationToken ct = default);
    Task<PaymentResponse?> UpdateAsync(int portfolioId, int id, UpdatePaymentRequest request, CancellationToken ct = default);
    Task<PaymentResponse?> MarkPaidAsync(int portfolioId, int id, MarkPaidRequest request, CancellationToken ct = default);
    Task<MarkLeasePastDuePaidResponse?> MarkLeasePastDuePaidAsync(int portfolioId, int leaseId, MarkPaidRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
