using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Read-only access to the portfolio's append-only <see cref="Core.Entities.AuditLog"/> trail.
/// User-facing queries consume a server-validated workspace scope and apply current-session,
/// access-revision, <c>reports.read</c>, and property scope in the translated SQL statement. The
/// separate forensic operations are platform-operator-only and retain an explicit portfolio scope.
/// </summary>
public interface IAuditQueryService
{
    Task<IReadOnlyList<AuditEntryResponse>> ListAsync(
        WorkspaceReadScope scope,
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

    /// <summary>
    /// Streams the admin-forensic rows for the same filters (search + operation + entityType +
    /// entityId) as <see cref="ListForensicAsync"/> but with paging ignored, so the CSV export covers
    /// the whole currently-filtered set. Rows are yielded as they arrive from Postgres; an unbounded
    /// result set is never buffered in memory.
    /// </summary>
    IAsyncEnumerable<AdminAuditEntryResponse> StreamForensicAsync(
        int portfolioId,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query,
        CancellationToken ct = default);
}
