using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAuditQueryService"/>
public class AuditQueryService : IAuditQueryService
{
    private readonly RentalCommandDbContext _db;
    private readonly AuditDescriber _describer;
    private readonly AuditDiffBuilder _diff;
    private readonly IAppTimeZoneProvider _tz;
    private readonly TimeProvider _timeProvider;

    public AuditQueryService(
        RentalCommandDbContext db,
        AuditDescriber describer,
        AuditDiffBuilder diff,
        IAppTimeZoneProvider tz,
        TimeProvider timeProvider)
    {
        _db = db;
        _describer = describer;
        _diff = diff;
        _tz = tz;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<AuditEntryResponse>> ListAsync(
        WorkspaceReadScope scope,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query,
        CancellationToken ct = default)
    {
        var rows = await BuildPageProjectionQuery(
                scope, operation, entityType, entityId, query)
            .ToListAsync(ct);

        return rows
            .Select(r => AuditEntryResponse.FromEntity(
                r.Audit,
                _describer,
                _diff,
                r.ResolvedActorName,
                r.UnitId))
            .ToList();
    }

    public async Task<IReadOnlyList<AdminAuditEntryResponse>> ListForensicAsync(
        int portfolioId,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query,
        CancellationToken ct = default)
    {
        var rows = await BuildForensicPageProjectionQuery(
                portfolioId, operation, entityType, entityId, query)
            .ToListAsync(ct);

        return rows
            .Select(r => AdminAuditEntryResponse.FromEntity(
                r.Audit,
                _describer,
                r.ResolvedActorName,
                r.UnitId))
            .ToList();
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
        var q = ApplyFilters(
                _db.AtomicAuditLogs.AsNoTracking().Where(audit => audit.PortfolioId == portfolioId),
                operation,
                entityType,
                entityId,
                query)
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id);

        // Actor resolution and supported entity-to-Unit route context stay in the same translated
        // SQL statement as the streamed audit row. No actor-id pre-pass, dictionary join, or
        // per-entity follow-up query is permitted here.
        await foreach (var row in ProjectRows(q, portfolioId).AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return AdminAuditEntryResponse.FromEntity(
                row.Audit,
                _describer,
                row.ResolvedActorName,
                row.UnitId);
        }
    }

    /// <summary>
    /// Builds the complete portfolio-scoped audit page as one translated SQL statement. PostgreSQL
    /// applies filters, stable sorting, paging, actor resolution, and every supported entity-to-Unit
    /// route lookup before rows cross the application boundary.
    /// </summary>
    internal IQueryable<AuditReadRow> BuildPageProjectionQuery(
        WorkspaceReadScope scope,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query)
    {
        var authorized = _db.AtomicAuditLogs
            .AsNoTracking()
            .WhereAuthorizedForReports(
                _db,
                scope,
                _timeProvider.GetUtcNow().UtcDateTime);

        return ProjectRows(
            FilteredPage(authorized, operation, entityType, entityId, query),
            scope.PortfolioId);
    }

    /// <summary>
    /// Platform-forensic projection. This deliberately does not share the user-facing authorization
    /// query: the platform policy admits the operation, while the explicit portfolio predicate remains
    /// the cross-workspace boundary. Unsupported and workspace-global rows remain visible here for
    /// diagnostics and export.
    /// </summary>
    internal IQueryable<AuditReadRow> BuildForensicPageProjectionQuery(
        int portfolioId,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query)
    {
        var portfolioRows = _db.AtomicAuditLogs
            .AsNoTracking()
            .Where(audit => audit.PortfolioId == portfolioId);
        return ProjectRows(
            FilteredPage(portfolioRows, operation, entityType, entityId, query),
            portfolioId);
    }

    private IQueryable<AuditReadRow> ProjectRows(
        IQueryable<Core.Entities.AtomicAuditLog> query,
        int portfolioId) =>
        query.Select(audit => new AuditReadRow
        {
            Audit = audit,
            ResolvedActorName = audit.ActorLabel != null && audit.ActorLabel != ""
                ? audit.ActorLabel
                : audit.UserId != null
                    ? _db.Users
                        .Where(user => user.Id == audit.UserId.Value
                            && user.WorkspaceAccessContexts.Any(
                                context => context.PortfolioId == portfolioId))
                        .Select(user => user.DisplayName != null && user.DisplayName != ""
                            ? user.DisplayName
                            : user.Email)
                        .FirstOrDefault()
                    : null,
            UnitId = audit.EntityType == nameof(Core.Entities.LeaseManagement)
                ? _db.LeaseManagements
                    .Where(relationship => relationship.Id == audit.EntityId
                        && relationship.PortfolioId == portfolioId)
                    .Select(relationship => (int?)relationship.UnitId)
                    .FirstOrDefault()
                : audit.EntityType == nameof(Core.Entities.LeaseAgreement)
                    ? _db.LeaseAgreements
                        .Where(agreement => agreement.Id == audit.EntityId
                            && agreement.PortfolioId == portfolioId)
                        .Select(agreement => (int?)agreement.LeaseManagement!.UnitId)
                        .FirstOrDefault()
                    : audit.EntityType == nameof(Core.Entities.TenantAccount)
                        ? _db.TenantAccounts
                            .Where(account => account.Id == audit.EntityId
                                && account.PortfolioId == portfolioId)
                            .Select(account => (int?)account.LeaseManagement!.UnitId)
                            .FirstOrDefault()
                        : audit.EntityType == nameof(Core.Entities.WorkOrder)
                            ? _db.WorkOrders
                                .Where(workOrder => workOrder.Id == audit.EntityId
                                    && workOrder.PortfolioId == portfolioId)
                                .Select(workOrder => workOrder.UnitId)
                                .FirstOrDefault()
                            : audit.EntityType == nameof(Core.Entities.Expense)
                                ? _db.Expenses
                                    .Where(expense => expense.Id == audit.EntityId
                                        && expense.PortfolioId == portfolioId)
                                    .Select(expense => expense.UnitId
                                        ?? (expense.WorkOrder != null ? expense.WorkOrder.UnitId : null))
                                    .FirstOrDefault()
                                : audit.EntityType == nameof(Core.Entities.RentalApplication)
                                    ? _db.RentalApplications
                                        .Where(application => application.Id == audit.EntityId
                                            && application.PortfolioId == portfolioId)
                                        .Select(application => (int?)application.UnitId)
                                        .FirstOrDefault()
                                    : null,
        });

    /// <summary>
    /// Filters, stably sorts, and pages an already-scoped source. The user-facing caller supplies its
    /// canonical authorization query; the forensic caller supplies its explicit portfolio predicate.
    /// </summary>
    private IQueryable<Core.Entities.AtomicAuditLog> FilteredPage(
        IQueryable<Core.Entities.AtomicAuditLog> source,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query)
    {
        var q = ApplyFilters(source, operation, entityType, entityId, query);

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
    /// Applies every column/free-text filter to an already-scoped source, but no ordering or paging.
    /// Shared by the paged viewer (<see cref="FilteredPage"/>) and the streamed export so the export's
    /// filtered set matches the page exactly.
    /// </summary>
    private IQueryable<Core.Entities.AtomicAuditLog> ApplyFilters(
        IQueryable<Core.Entities.AtomicAuditLog> source,
        AuditLogOperation? operation,
        string? entityType,
        int? entityId,
        ListQuery query)
    {
        var q = source;

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

        // Grid date-range filter on Timestamp — a true INSTANT (real time-of-day), so the picked
        // [from, to] day span is interpreted in the landlord's business timezone and converted to UTC
        // instants (half-open). "This month" is the landlord's local month: an 11pm-ET-Dec-31 event
        // stays in December, not the next UTC year. DB-side, one predicate.
        var (fromUtc, toUtcExclusive) = ListDateRange.BusinessTzDay(query.From, query.To, _tz.BusinessTimeZone);
        if (fromUtc is { } f)
            q = q.Where(a => a.Timestamp >= f);
        if (toUtcExclusive is { } t)
            q = q.Where(a => a.Timestamp < t);

        return q;
    }

    /// <summary>
    /// Free-text search across <em>every field the audit page renders</em>: the action title / entity
    /// noun (derived from <see cref="Core.Entities.AtomicAuditLog.EntityType"/>), the actor (label or
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
    private static IQueryable<Core.Entities.AtomicAuditLog> ApplySearch(IQueryable<Core.Entities.AtomicAuditLog> q, string term)
    {
        // Case-insensitive contains via Postgres ILIKE %term%, served by the
        // `gin (lower(col) gin_trgm_ops)` trigram indexes (AuditSearchTrgmIndexes) — index-driven,
        // never a sequential scan. Only the three trgm-indexed columns appear here; the unindexable
        // computed-column LIKEs that used to be OR'd in (and forced a seq scan of the whole predicate)
        // are gone, replaced by id equality below. The audit trail is Postgres-only.
        var like = $"%{term}%";

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
            EF.Functions.ILike(a.EntityType, like)
            || (a.ActorLabel != null && EF.Functions.ILike(a.ActorLabel, like))
            || (a.IpAddress != null && EF.Functions.ILike(a.IpAddress, like))
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

    internal sealed class AuditReadRow
    {
        public Core.Entities.AtomicAuditLog Audit { get; set; } = null!;
        public string? ResolvedActorName { get; set; }
        public int? UnitId { get; set; }
    }
}
