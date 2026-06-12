using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAuditQueryService"/>
public class AuditQueryService : IAuditQueryService
{
    private readonly RentalCommandDbContext _db;
    private readonly AuditDescriber _describer;
    private readonly AuditDiffBuilder _diff;

    public AuditQueryService(RentalCommandDbContext db, AuditDescriber describer, AuditDiffBuilder diff)
    {
        _db = db;
        _describer = describer;
        _diff = diff;
    }

    public async Task<IReadOnlyList<AuditEntryResponse>> ListAsync(
        int portfolioId,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query,
        CancellationToken ct = default)
    {
        var rows = await FilteredPage(portfolioId, operation, entityType, entityId, query).ToListAsync(ct);
        return rows.Select(r => AuditEntryResponse.FromEntity(r, _describer, _diff)).ToList();
    }

    public async Task<IReadOnlyList<AdminAuditEntryResponse>> ListForensicAsync(
        int portfolioId,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query,
        CancellationToken ct = default)
    {
        var rows = await FilteredPage(portfolioId, operation, entityType, entityId, query).ToListAsync(ct);
        return rows.Select(r => AdminAuditEntryResponse.FromEntity(r, _describer)).ToList();
    }

    /// <summary>
    /// Portfolio-scoped, filtered, sorted, paged query shared by the landlord-facing and admin-forensic
    /// projections. The cross-tenant IDOR guard (<c>PortfolioId == portfolioId</c>) is applied here, so
    /// neither caller can ever read another tenant's trail.
    /// </summary>
    private IQueryable<Core.Entities.AuditLog> FilteredPage(
        int portfolioId, AuditLogOperation? operation, string? entityType, int? entityId, ListQuery query)
    {
        var q = _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId);

        if (operation.HasValue)
        {
            q = q.Where(a => a.Operation == operation.Value);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var et = entityType.Trim();
            q = q.Where(a => a.EntityType == et);
        }

        if (entityId.HasValue)
        {
            q = q.Where(a => a.EntityId == entityId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // The humanized description is computed post-query, so server-side search runs over the
            // raw actor + entity type (which is what the description is derived from).
            var term = query.Search.Trim();
            q = q.Where(a =>
                (a.ActorLabel != null && EF.Functions.ILike(a.ActorLabel, $"%{term}%")) ||
                EF.Functions.ILike(a.EntityType, $"%{term}%"));
        }

        // Default newest-first; ascending only when the client explicitly asks for `timestamp`.
        q = query.SortField switch
        {
            "timestamp" => query.SortDescending
                ? q.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
                : q.OrderBy(a => a.Timestamp).ThenBy(a => a.Id),
            _ => q.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id),
        };

        return q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);
    }
}
