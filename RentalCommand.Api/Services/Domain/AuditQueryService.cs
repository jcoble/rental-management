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
        var userNames = await ResolveActorNamesAsync(portfolioId, rows, ct);
        return rows.Select(r => AuditEntryResponse.FromEntity(r, _describer, _diff, userNames)).ToList();
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
        var userNames = await ResolveActorNamesAsync(portfolioId, rows, ct);
        return rows.Select(r => AdminAuditEntryResponse.FromEntity(r, _describer, userNames)).ToList();
    }

    /// <summary>
    /// Batch-resolves a human label (display name, falling back to email) for every user-actor row
    /// that didn't carry an <c>ActorLabel</c> of its own, in ONE query. The audit trail stores only
    /// the user id for HTTP requests whose token lacked a name claim (and the trail must not break
    /// when a user later renames), so the friendly label is resolved at read time — that's what keeps
    /// the History card from showing "User #1". Rows that already carry an <c>ActorLabel</c>, or that
    /// have no user id (system/AI actors), are skipped. Empty when there is nothing to resolve.
    /// The resolution is scoped to <paramref name="portfolioId"/> as a defensive guard so an actor
    /// email from another portfolio can never surface through the History card.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>> ResolveActorNamesAsync(
        int portfolioId, IReadOnlyList<Core.Entities.AuditLog> rows, CancellationToken ct)
    {
        var ids = rows
            .Where(r => string.IsNullOrWhiteSpace(r.ActorLabel) && r.UserId.HasValue)
            .Select(r => r.UserId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return EmptyUserNames;
        }

        var resolved = await _db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.PortfolioId == portfolioId)
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .ToListAsync(ct);

        return resolved.ToDictionary(
            u => u.Id,
            u => !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName : (u.Email ?? string.Empty));
    }

    private static readonly IReadOnlyDictionary<int, string> EmptyUserNames =
        new Dictionary<int, string>();

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

        // Friendly-actor resolution for the stream: a single up-front id→label pass over the same
        // filtered set (projecting only the user ids, never the rows) so each streamed row can show
        // a real name instead of "User #1" without buffering the full result set. The user table is
        // tiny relative to the audit trail, so the map fits comfortably in memory.
        var actorUserIds = await q
            .Where(a => (a.ActorLabel == null || a.ActorLabel == "") && a.UserId != null)
            .Select(a => a.UserId!.Value)
            .Distinct()
            .ToListAsync(ct);

        IReadOnlyDictionary<int, string> userNames = EmptyUserNames;
        if (actorUserIds.Count > 0)
        {
            var resolved = await _db.Users
                .AsNoTracking()
                .Where(u => actorUserIds.Contains(u.Id) && u.PortfolioId == portfolioId)
                .Select(u => new { u.Id, u.DisplayName, u.Email })
                .ToListAsync(ct);
            userNames = resolved.ToDictionary(
                u => u.Id,
                u => !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName : (u.Email ?? string.Empty));
        }

        q = q.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id);

        await foreach (var row in q.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return AdminAuditEntryResponse.FromEntity(row, _describer, userNames);
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
    /// the IP address. Runs entirely Postgres-side as one translated query.
    /// <para>
    /// The text predicates only touch the columns that carry pg_trgm GIN indexes
    /// (<c>EntityType</c>/<c>ActorLabel</c>/<c>IpAddress</c>, see migration <c>AuditSearchTrgmIndexes</c>),
    /// so Postgres can BitmapOr the index-backed branches. The entity-id / actor-id / compound-label
    /// matching is done as <em>integer equality</em> on a parsed id (<c>EntityId == n</c> /
    /// <c>UserId == n</c>) rather than <c>LIKE</c> over <c>EntityId::text</c>: the old computed-column
    /// LIKEs were unindexable and, OR'd into the predicate, forced the whole search to a sequential scan
    /// (defeating the trgm indexes). A purely-numeric term ("76") matches the id; a "noun + number"
    /// term ("expense 76", "Expense #76") matches the entity noun on the trgm column AND the id.
    /// </para>
    /// </summary>
    private static IQueryable<Core.Entities.AuditLog> ApplySearch(IQueryable<Core.Entities.AuditLog> q, string term)
    {
        // Case-insensitive contains via LOWER(col) LIKE %lowered-term%. This shape translates on both
        // Npgsql and SQLite, and on Postgres is served by the `gin (lower(col) gin_trgm_ops)` trigram
        // indexes (AuditSearchTrgmIndexes) — index-driven, never a sequential scan. Only the three
        // trgm-indexed columns appear here; the unindexable computed-column LIKEs that used to be OR'd
        // in (and forced a seq scan of the whole predicate) are gone, replaced by id equality below.
        var like = $"%{term.ToLower()}%";

        // Verb search: "Created", "create", "recorded", "scheduled", etc. should narrow by operation.
        // Match against both the enum name (Created/Updated/Deleted/Approved/Rejected) and the friendly
        // verbs the describer uses so a user can search by what they SEE.
        var matchedOps = MatchOperations(term);

        // Pull any integer out of the term for index-friendly id equality: "76", "#76", "expense 76",
        // "User #1" all yield 76 / 1. Null when the term carries no digits. This replaces the old
        // LIKE-over-EntityId::text / "Type #id" / "User #id" computed-column matches, which no index
        // could serve.
        var parsedId = ExtractFirstInteger(term);

        return q.Where(a =>
            EF.Functions.Like(a.EntityType.ToLower(), like)
            || (a.ActorLabel != null && EF.Functions.Like(a.ActorLabel.ToLower(), like))
            || (a.IpAddress != null && EF.Functions.Like(a.IpAddress.ToLower(), like))
            // Entity / actor id as integer equality (sargable) instead of LIKE on its text form.
            || (parsedId != null && a.EntityId == parsedId.Value)
            || (parsedId != null && a.UserId != null && a.UserId == parsedId.Value)
            // Action verb → operation.
            || matchedOps.Contains(a.Operation));
    }

    /// <summary>
    /// Extracts the first run of digits from a free-text term as an int (e.g. "Expense #76" → 76,
    /// "user 1" → 1). Returns null when the term has no digits or the number overflows an int.
    /// </summary>
    private static int? ExtractFirstInteger(string term)
    {
        var match = System.Text.RegularExpressions.Regex.Match(term, @"\d+");
        if (!match.Success) return null;
        return int.TryParse(match.Value, out var n) ? n : null;
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
