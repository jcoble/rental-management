using System.Globalization;
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

    public async IAsyncEnumerable<AdminAuditEntryResponse> StreamForensicAsync(
        int portfolioId,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Export honors the same filters (search + operation + entityType + entityId) as the page, but
        // ignores paging (skip/take) so the *whole* filtered result set is exported. The query is
        // streamed row-by-row from Postgres (AsAsyncEnumerable) so an unbounded set is never
        // materialized in API memory; the caller (CSV writer) flushes each row as it arrives.
        var q = ApplyFilters(portfolioId, operation, entityType, entityId, query);
        q = q.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id);

        await foreach (var row in q.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return AdminAuditEntryResponse.FromEntity(row, _describer);
        }
    }

    /// <summary>
    /// Portfolio-scoped, filtered, sorted, paged query shared by the landlord-facing and admin-forensic
    /// projections. The cross-tenant IDOR guard (<c>PortfolioId == portfolioId</c>) is applied here, so
    /// neither caller can ever read another tenant's trail.
    /// </summary>
    private IQueryable<Core.Entities.AuditLog> FilteredPage(
        int portfolioId, AuditLogOperation? operation, string? entityType, int? entityId, ListQuery query)
    {
        var q = ApplyFilters(portfolioId, operation, entityType, entityId, query);

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

    /// <summary>
    /// Applies the portfolio scope and every column/free-text filter, but no ordering or paging.
    /// Shared by the paged viewer (<see cref="FilteredPage"/>) and the streamed export so the export's
    /// filtered set matches the page exactly.
    /// </summary>
    private IQueryable<Core.Entities.AuditLog> ApplyFilters(
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
            q = ApplySearch(q, query.Search.Trim());
        }

        return q;
    }

    /// <summary>
    /// Free-text search across <em>every field the audit page renders</em>: the action title / entity
    /// noun (derived from <see cref="Core.Entities.AuditLog.EntityType"/>), the actor (label or
    /// <c>User #id</c>), the entity id (so a bare <c>76</c> matches), the compound entity label
    /// (<c>Expense #76</c> / <c>expense 76</c>), the action verb (<c>Created</c>/<c>Updated</c>…), and
    /// the IP address. Runs entirely Postgres-side as one translated query — pg_trgm GIN indexes on the
    /// searchable text columns (see migration <c>AuditSearchTrgmIndexes</c>) keep the <c>ILIKE %term%</c>
    /// scans index-backed rather than sequential, so it scales past the in-memory filtering it replaced.
    /// </summary>
    private static IQueryable<Core.Entities.AuditLog> ApplySearch(IQueryable<Core.Entities.AuditLog> q, string term)
    {
        // Case-insensitive contains via LOWER(col) LIKE LOWER(%term%). This shape translates on both
        // Npgsql and SQLite, and on Postgres is backed by the `gin (lower(col) gin_trgm_ops)` trigram
        // indexes added in the AuditSearchTrgmIndexes migration — so the scan stays index-driven, never
        // sequential, and never pulls rows into memory to filter.
        var like = $"%{term.ToLower()}%";

        // Verb search: "Created", "create", "recorded", "scheduled", etc. should narrow by operation.
        // Match against both the enum name (Created/Updated/Deleted/Approved/Rejected) and the friendly
        // verbs the describer uses so a user can search by what they SEE.
        var matchedOps = MatchOperations(term);

        return q.Where(a =>
            EF.Functions.Like(a.EntityType.ToLower(), like)
            || (a.ActorLabel != null && EF.Functions.Like(a.ActorLabel.ToLower(), like))
            || (a.IpAddress != null && EF.Functions.Like(a.IpAddress.ToLower(), like))
            // Entity id: bare numeric ("76") and as text inside a larger term.
            || EF.Functions.Like(a.EntityId.ToString(), like)
            // Compound entity label exactly as rendered: "Expense #76" and "Expense 76".
            || EF.Functions.Like((a.EntityType + " #" + a.EntityId.ToString()).ToLower(), like)
            || EF.Functions.Like((a.EntityType + " " + a.EntityId.ToString()).ToLower(), like)
            // Actor as rendered for user actors: "User #1".
            || (a.UserId != null && EF.Functions.Like(("User #" + a.UserId.ToString()).ToLower(), like))
            // Action verb → operation.
            || matchedOps.Contains(a.Operation));
    }

    /// <summary>
    /// Resolves a free-text term to the set of <see cref="AuditLogOperation"/>s whose enum name OR
    /// friendly verb the term is a prefix of, so searching "create", "recorded", or "schedule" filters
    /// to the matching action(s). Returns empty when the term doesn't look like a verb.
    /// </summary>
    private static IReadOnlyList<AuditLogOperation> MatchOperations(string term)
    {
        var t = term.ToLowerInvariant();
        var matched = new List<AuditLogOperation>();

        foreach (var op in Enum.GetValues<AuditLogOperation>())
        {
            // Enum name match (Created/Updated/...).
            if (op.ToString().ToLowerInvariant().Contains(t))
            {
                matched.Add(op);
                continue;
            }

            // Friendly verbs the describer renders for Created rows.
            var friendly = op switch
            {
                AuditLogOperation.Created => new[] { "recorded", "added", "created", "scheduled", "received", "held" },
                AuditLogOperation.Updated => new[] { "updated", "changed", "edited" },
                AuditLogOperation.Deleted => new[] { "deleted", "removed" },
                AuditLogOperation.Approved => new[] { "approved" },
                AuditLogOperation.Rejected => new[] { "rejected", "declined" },
                _ => Array.Empty<string>(),
            };

            if (friendly.Any(v => v.Contains(t)))
            {
                matched.Add(op);
            }
        }

        return matched;
    }
}
