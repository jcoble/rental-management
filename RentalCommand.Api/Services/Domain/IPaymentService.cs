using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Canonical receipt reads plus the remaining legacy import mutation surface. Receipt reads receive the
/// signed access-envelope claims as an opaque authorization input; <see cref="PaymentService"/> validates
/// the current session, revision, capability, and assigned property inside the same database query that
/// filters, sorts, pages, and projects the receipt rows.
/// </summary>
public interface IPaymentService
{
    Task<IReadOnlyList<PaymentReceiptResponse>> ListAsync(
        PaymentReceiptReadContext access, PaymentListQuery query, CancellationToken ct = default);
    Task<PaymentListResponse> ListPageAsync(
        PaymentReceiptReadContext access, PaymentListQuery query, CancellationToken ct = default);
    Task<PaymentReceiptResponse?> GetAsync(
        PaymentReceiptReadContext access, long id, CancellationToken ct = default);
    Task<PaymentResponse?> CreateAsync(int portfolioId, CreatePaymentRequest request, CancellationToken ct = default);
}

/// <summary>
/// Untrusted access-envelope values presented by the signed-in caller. Possessing these values grants
/// nothing by itself; the receipt query correlates them to the active database session and current access
/// revision before any receipt can be counted or returned.
/// </summary>
public readonly record struct PaymentReceiptReadContext(
    int PortfolioId,
    int UserId,
    Guid SessionId,
    int AccessContextId,
    long AccessRevision);
