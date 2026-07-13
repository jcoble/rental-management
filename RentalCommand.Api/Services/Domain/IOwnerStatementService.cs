using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Builds read-only per-owner, per-year financial statements (income, expenses, management fee,
/// net distribution) from existing Payments and Expenses — no new records are created.
/// </summary>
public interface IOwnerStatementService
{
    Task<OwnerStatementReport?> GetForOwnerAsync(
        WorkspaceReadScope scope, int ownerId, int year, CancellationToken ct = default);

    Task<IReadOnlyList<OwnerStatementSummary>> ListOwnersWithNetAsync(
        WorkspaceReadScope scope, int year, CancellationToken ct = default);

    Task<decimal> GetTotalNetToOwnersAsync(
        WorkspaceReadScope scope, int year, CancellationToken ct = default);

}
