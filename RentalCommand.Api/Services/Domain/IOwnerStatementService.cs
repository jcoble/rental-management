using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Builds read-only per-owner, per-year financial statements (income, expenses, management fee,
/// net distribution) from existing Payments and Expenses — no new records are created.
/// </summary>
public interface IOwnerStatementService
{
    /// <summary>
    /// Returns the full owner statement for <paramref name="ownerId"/> for <paramref name="year"/>,
    /// or <c>null</c> if the owner is not found in the portfolio.
    /// </summary>
    Task<OwnerStatementReport?> GetForOwnerAsync(int portfolioId, int ownerId, int year, CancellationToken ct = default);

    /// <summary>
    /// Returns one summary entry per owner that has at least one property in the portfolio,
    /// with that owner's net distribution for <paramref name="year"/>. Used by the picker/list UI.
    /// </summary>
    Task<IReadOnlyList<OwnerStatementSummary>> ListOwnersWithNetAsync(int portfolioId, int year, CancellationToken ct = default);

    /// <summary>
    /// Returns the portfolio-level owner net distribution total for <paramref name="year"/>, summed in SQL.
    /// </summary>
    Task<decimal> GetTotalNetToOwnersAsync(int portfolioId, int year, CancellationToken ct = default);
}
