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
    /// Tenant-facing ledger for one lease: all charges and payments, newest first, each with a
    /// plain-English explanation of what it is, plus a running balance. Returns null when the lease is
    /// not found in the portfolio.
    /// </summary>
    Task<LeaseLedgerResponse?> GetLedgerAsync(
        int portfolioId, int id, int? restrictToTenantId = null, CancellationToken ct = default);
    Task<LeaseResponse?> CreateAsync(int portfolioId, CreateLeaseRequest request, CancellationToken ct = default);
    Task<LeaseResponse?> UpdateAsync(int portfolioId, int id, UpdateLeaseRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Generates a standard residential lease agreement PDF from the lease's captured terms (the
    /// "5-question generator"), stores it as a <see cref="Core.Entities.StoredFile"/> (entityType=Lease,
    /// entityId=lease id), and returns a reference to the stored document. Returns null when the lease is
    /// not in the caller's portfolio.
    /// </summary>
    Task<LeaseDocumentResponse?> GenerateDocumentAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Returns lightweight generated-agreement metadata for the lease. Returns null only when the lease is
    /// not in the caller's portfolio; missing agreement is represented by <c>HasDocument=false</c>.
    /// </summary>
    Task<LeaseDocumentStatusResponse?> GetDocumentStatusAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Streams the latest generated lease agreement PDF for the lease. Returns null when the lease is not
    /// in the portfolio or no agreement has been generated yet (controller maps to 404).
    /// </summary>
    Task<(Stream Stream, string FileName, string ContentType)?> GetDocumentAsync(int portfolioId, int id, CancellationToken ct = default);
}
