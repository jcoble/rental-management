using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Read-only access to the portfolio's append-only <see cref="Core.Entities.AuditLog"/> trail.
/// Every query is filtered by the caller's portfolio id (cross-tenant IDOR guard); there are no
/// create/update/delete operations.
/// </summary>
public interface IAuditQueryService
{
    Task<IReadOnlyList<AuditEntryResponse>> ListAsync(
        int portfolioId,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// Admin-only forensic variant of <see cref="ListAsync"/>: same portfolio-scoped filters, but the
    /// rows include the IP address and raw old→new JSON withheld from the landlord-facing projection.
    /// </summary>
    Task<IReadOnlyList<AdminAuditEntryResponse>> ListForensicAsync(
        int portfolioId,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query,
        CancellationToken ct = default);
}
