using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Lease"/>. Every query is filtered by the caller's
/// portfolio id (sourced from the JWT claim, never a client parameter). Create verifies the referenced
/// property, unit, and tenant all belong to the portfolio before attaching the lease; <see cref="CreateAsync"/>
/// returns null when any reference is out of scope. Soft-delete is used for removal. Create/update/delete
/// broadcast realtime updates via <see cref="Core.Interfaces.IDataUpdateService"/>.
/// </summary>
public interface ILeaseService
{
    Task<IReadOnlyList<LeaseResponse>> ListAsync(int portfolioId, int? tenantId, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<LeaseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Tenant-facing ledger for one lease: all charges and payments, newest first, each with a
    /// plain-English explanation of what it is, plus a running balance. Returns null when the lease is
    /// not found in the portfolio.
    /// </summary>
    Task<LeaseLedgerResponse?> GetLedgerAsync(
        int portfolioId, int id, int? restrictToTenantId = null, CancellationToken ct = default);
    Task<LeaseResponse?> CreateAsync(int portfolioId, CreateLeaseRequest request, CancellationToken ct = default);
    Task<LeaseResponse?> UpdateAsync(int portfolioId, int id, UpdateLeaseRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
