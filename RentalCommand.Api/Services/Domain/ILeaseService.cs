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
    Task<LeaseListResponse> ListPageAsync(int portfolioId, LeaseListQuery query, CancellationToken ct = default);
    Task<LeaseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Tenant-facing ledger for one LeaseManagement's continuous TenantAccount. The lookup, totals,
    /// authorization, ordering, and paging are all canonical database-side projections.
    /// </summary>
    Task<LeaseLedgerResponse?> GetLedgerAsync(
        int portfolioId, int leaseManagementId, int? restrictToTenantId = null, int skip = 0, int? take = null, CancellationToken ct = default);
}
