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
}
