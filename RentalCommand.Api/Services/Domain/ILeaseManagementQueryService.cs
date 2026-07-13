using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Canonical relationship reads. The signed access coordinates are untrusted inputs; every query
/// revalidates the current session, access revision, capability, and property scope in PostgreSQL.
/// </summary>
public interface ILeaseManagementQueryService
{
    Task<LeaseManagementListResponse> ListPageAsync(
        LeaseManagementReadContext access, LeaseManagementListQuery query,
        CancellationToken ct = default);
    Task<LeaseManagementDetailResponse?> GetAsync(
        LeaseManagementReadContext access, int leaseManagementId,
        CancellationToken ct = default);
    Task<LeaseLedgerResponse?> GetLedgerAsync(
        LeaseManagementReadContext access, int leaseManagementId, int skip = 0, int? take = null,
        CancellationToken ct = default);
}

public readonly record struct LeaseManagementReadContext(
    int PortfolioId,
    int UserId,
    Guid SessionId,
    int AccessContextId,
    long AccessRevision);
