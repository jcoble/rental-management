using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Outcome of attempting to create an opening balance, so the controller can map each case to the right
/// HTTP status without leaking which leases exist in other portfolios.
/// </summary>
public enum CreateOpeningBalanceResult
{
    /// <summary>Created successfully.</summary>
    Created,
    /// <summary>The referenced lease is not in the caller's portfolio (treat as 404).</summary>
    LeaseNotFound,
    /// <summary>An opening balance already exists for this lease (treat as 409 — PATCH it instead).</summary>
    AlreadyExists,
}

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.OpeningBalance"/> — the per-lease balance carried
/// over from before the landlord migrated onto Rental Command. At most one row per lease; create rejects
/// a duplicate (the caller should PATCH the existing one). Every query is filtered by the caller's
/// portfolio; mutations broadcast realtime updates.
/// </summary>
public interface IOpeningBalanceService
{
    Task<IReadOnlyList<OpeningBalanceResponse>> ListAsync(int portfolioId, int? leaseId, CancellationToken ct = default);
    Task<OpeningBalanceResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<(CreateOpeningBalanceResult Result, OpeningBalanceResponse? Response)> CreateAsync(
        int portfolioId, CreateOpeningBalanceRequest request, CancellationToken ct = default);
    Task<OpeningBalanceResponse?> UpdateAsync(int portfolioId, int id, UpdateOpeningBalanceRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
