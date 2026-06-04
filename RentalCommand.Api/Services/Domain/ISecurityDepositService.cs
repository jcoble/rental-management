using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Manages security deposit holdings through their lifecycle: held → deductions applied → returned.
/// All operations are portfolio-scoped.
/// </summary>
public interface ISecurityDepositService
{
    /// <summary>List all holdings for the portfolio, optionally filtered by lease.</summary>
    Task<IReadOnlyList<SecurityDepositResponse>> ListAsync(int portfolioId, int? leaseId, CancellationToken ct = default);

    /// <summary>Get a single holding by id. Returns null when not found or out of scope.</summary>
    Task<SecurityDepositResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Create a new holding. Amount defaults to the lease's SecurityDeposit when not supplied.
    /// Returns null when the lease is not in the caller's portfolio.
    /// </summary>
    Task<SecurityDepositResponse?> CreateAsync(int portfolioId, CreateDepositRequest request, CancellationToken ct = default);

    /// <summary>
    /// Append an itemised deduction. Returns null when the holding is not found or already returned.
    /// </summary>
    Task<SecurityDepositResponse?> AddDeductionAsync(int portfolioId, int id, AddDeductionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Finalise the return: compute net refund, set ReturnedAmount/ReturnedAt, transition Status.
    /// Returns null when the holding is not found or already returned.
    /// </summary>
    Task<SecurityDepositResponse?> ReturnAsync(int portfolioId, int id, ReturnDepositRequest request, CancellationToken ct = default);

    /// <summary>
    /// Render an itemised, photo-backed move-out statement PDF for the holding: deposit held, each
    /// deduction, any photos attached to the deposit, and the net refund (or amount owed). Returns null
    /// when the holding is not found or out of scope.
    /// </summary>
    Task<byte[]?> GetMoveOutStatementAsync(int portfolioId, int id, CancellationToken ct = default);
}
