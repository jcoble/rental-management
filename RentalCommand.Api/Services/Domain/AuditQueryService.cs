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

    public AuditQueryService(RentalCommandDbContext db, AuditDescriber describer)
    {
        _db = db;
        _describer = describer;
    }

    public async Task<IReadOnlyList<AuditEntryResponse>> ListAsync(
        int portfolioId,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query,
        CancellationToken ct = default)
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

        var rows = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return rows.Select(r => AuditEntryResponse.FromEntity(r, _describer)).ToList();
    }
}
