using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class EvictionCaseService : IEvictionCaseService
{
    private const string EntityType = "EvictionCase";
    private const string EventEntityType = "EvictionCaseEvent";
    private const string LeaseEntityType = "Lease";
    private const string UnitEntityType = "Unit";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;

    public EvictionCaseService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<EvictionCaseResponse>> ListAsync(
        int portfolioId, EvictionCaseListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, query, ct);
        return page.Items;
    }

    public async Task<EvictionCaseListResponse> ListPageAsync(
        int portfolioId, EvictionCaseListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(portfolioId, query);
        var totalCount = await filtered.CountAsync(ct);
        var rows = await ProjectRows(ApplySort(filtered, query))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new EvictionCaseListResponse
        {
            Items = rows.Select(ToResponse).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<EvictionCaseResponse?> GetAsync(
        int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.EvictionCases
            .AsNoTracking()
            .Include(e => e.Lease)
            .Include(e => e.Property)
            .Include(e => e.Unit)
            .Include(e => e.Tenant)
            .Include(e => e.Events)
            .FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);

        return entity is null ? null : EvictionCaseResponse.FromEntity(entity, includeEvents: true);
    }

    public async Task<EvictionCaseResponse?> CreateAsync(
        int portfolioId, CreateEvictionCaseRequest request, CancellationToken ct = default)
    {
        var lease = await _db.Leases
            .Include(l => l.Unit)
            .FirstOrDefaultAsync(l => l.Id == request.LeaseId && l.PortfolioId == portfolioId, ct);
        if (lease is null)
            return null;

        var now = _timeProvider.UtcNow();
        var eventDate = ResolveInitialEventDate(request, now);
        var entity = new EvictionCase
        {
            PortfolioId = portfolioId,
            LeaseId = lease.Id,
            PropertyId = lease.PropertyId,
            UnitId = lease.UnitId,
            TenantId = lease.TenantId,
            Status = request.Status,
            FiledOnDate = request.FiledOnDate?.ToUtc().Date ?? (request.Status >= EvictionCaseStatus.Filed ? eventDate : null),
            HearingDate = request.HearingDate?.ToUtc().Date,
            CourtName = Normalize(request.CourtName),
            CaseNumber = Normalize(request.CaseNumber),
            Notes = Normalize(request.Notes),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var initialEvent = InitialEventForStatus(entity.Status);
        if (initialEvent.HasValue)
        {
            entity.Events.Add(new EvictionCaseEvent
            {
                PortfolioId = portfolioId,
                EventType = initialEvent.Value,
                EventDate = eventDate,
                Notes = string.IsNullOrWhiteSpace(entity.Notes) ? "Case opened." : entity.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        ApplyLeaseWorkflow(entity, lease, now, eventDate);

        await _db.EvictionCases.AddAsync(entity, ct);
        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? EvictionCaseResponse.FromEntity(entity, includeEvents: true);
        await BroadcastCaseAndRelatedAsync(portfolioId, entity.Id, response, lease, lease.Unit, ct);
        return response;
    }

    public async Task<EvictionCaseResponse?> UpdateAsync(
        int portfolioId, int id, UpdateEvictionCaseRequest request, CancellationToken ct = default)
    {
        var entity = await _db.EvictionCases
            .Include(e => e.Lease)
                .ThenInclude(l => l!.Unit)
            .FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
        if (entity is null || entity.Lease is null)
            return null;

        var now = _timeProvider.UtcNow();
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.FiledOnDate.HasValue) entity.FiledOnDate = request.FiledOnDate.Value.ToUtc().Date;
        if (request.HearingDate.HasValue) entity.HearingDate = request.HearingDate.Value.ToUtc().Date;
        if (request.ResolvedOnDate.HasValue) entity.ResolvedOnDate = request.ResolvedOnDate.Value.ToUtc().Date;
        if (request.CourtName != null) entity.CourtName = Normalize(request.CourtName);
        if (request.CaseNumber != null) entity.CaseNumber = Normalize(request.CaseNumber);
        if (request.Resolution != null) entity.Resolution = Normalize(request.Resolution);
        if (request.Notes != null) entity.Notes = Normalize(request.Notes);
        entity.UpdatedAt = now;

        ApplyLeaseWorkflow(entity, entity.Lease, now, entity.ResolvedOnDate ?? entity.FiledOnDate ?? now.Date);
        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? EvictionCaseResponse.FromEntity(entity, includeEvents: true);
        await BroadcastCaseAndRelatedAsync(portfolioId, entity.Id, response, entity.Lease, entity.Lease.Unit, ct);
        return response;
    }

    public async Task<EvictionCaseResponse?> AddEventAsync(
        int portfolioId, int id, CreateEvictionCaseEventRequest request, CancellationToken ct = default)
    {
        var entity = await _db.EvictionCases
            .Include(e => e.Lease)
                .ThenInclude(l => l!.Unit)
            .FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
        if (entity is null || entity.Lease is null)
            return null;

        var now = _timeProvider.UtcNow();
        var eventDate = request.EventDate.ToUtc().Date;
        var evt = new EvictionCaseEvent
        {
            PortfolioId = portfolioId,
            EvictionCaseId = entity.Id,
            EventType = request.EventType,
            EventDate = eventDate,
            Notes = Normalize(request.Notes),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.EvictionCaseEvents.Add(evt);

        ApplyEventToCase(entity, evt, now);
        ApplyLeaseWorkflow(entity, entity.Lease, now, eventDate);
        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? EvictionCaseResponse.FromEntity(entity, includeEvents: true);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EventEntityType, evt.Id, EvictionCaseEventResponse.FromEntity(evt), ct);
        await BroadcastCaseAndRelatedAsync(portfolioId, entity.Id, response, entity.Lease, entity.Lease.Unit, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.EvictionCases
            .Include(e => e.Events)
            .FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
        if (entity is null)
            return false;

        var now = _timeProvider.UtcNow();
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        foreach (var evt in entity.Events)
        {
            evt.DeletedAt = now;
            evt.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    private IQueryable<EvictionCase> BuildListQuery(int portfolioId, EvictionCaseListQuery query)
    {
        var q = _db.EvictionCases
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId);

        if (query.LeaseId.HasValue)
            q = q.Where(e => e.LeaseId == query.LeaseId.Value);
        if (query.PropertyId.HasValue)
            q = q.Where(e => e.PropertyId == query.PropertyId.Value);
        if (query.TenantId.HasValue)
            q = q.Where(e => e.TenantId == query.TenantId.Value);
        if (query.Status.HasValue)
            q = q.Where(e => e.Status == query.Status.Value);

        var (from, to) = ListDateRange.UtcDay(query.From, query.To);
        if (from is { } fromUtc)
            q = q.Where(e => (e.FiledOnDate ?? e.CreatedAt) >= fromUtc);
        if (to is { } toUtcExclusive)
            q = q.Where(e => (e.FiledOnDate ?? e.CreatedAt) < toUtcExclusive);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(e =>
                EF.Functions.ILike(e.Lease!.LeaseNumber, $"%{term}%") ||
                EF.Functions.ILike(e.Property!.Name, $"%{term}%") ||
                EF.Functions.ILike(e.Unit!.UnitNumber, $"%{term}%") ||
                EF.Functions.ILike(e.Tenant!.FirstName, $"%{term}%") ||
                EF.Functions.ILike(e.Tenant.LastName, $"%{term}%") ||
                EF.Functions.ILike(e.Tenant.FirstName + " " + e.Tenant.LastName, $"%{term}%") ||
                (e.CourtName != null && EF.Functions.ILike(e.CourtName, $"%{term}%")) ||
                (e.CaseNumber != null && EF.Functions.ILike(e.CaseNumber, $"%{term}%")) ||
                (e.Notes != null && EF.Functions.ILike(e.Notes, $"%{term}%")));
        }

        return q;
    }

    private static IQueryable<EvictionCase> ApplySort(IQueryable<EvictionCase> q, ListQuery query)
    {
        var ordered = query.SortField switch
        {
            "lease" or "leasenumber" => query.SortDescending ? q.OrderByDescending(e => e.Lease!.LeaseNumber) : q.OrderBy(e => e.Lease!.LeaseNumber),
            "property" or "propertyname" => query.SortDescending ? q.OrderByDescending(e => e.Property!.Name) : q.OrderBy(e => e.Property!.Name),
            "unit" or "unitnumber" => query.SortDescending ? q.OrderByDescending(e => e.Unit!.UnitNumber) : q.OrderBy(e => e.Unit!.UnitNumber),
            "tenant" or "tenantname" => query.SortDescending ? q.OrderByDescending(e => e.Tenant!.LastName).ThenByDescending(e => e.Tenant!.FirstName) : q.OrderBy(e => e.Tenant!.LastName).ThenBy(e => e.Tenant!.FirstName),
            "status" => query.SortDescending ? q.OrderByDescending(e => e.Status) : q.OrderBy(e => e.Status),
            "hearingdate" => query.SortDescending ? q.OrderByDescending(e => e.HearingDate) : q.OrderBy(e => e.HearingDate),
            "resolvedondate" => query.SortDescending ? q.OrderByDescending(e => e.ResolvedOnDate) : q.OrderBy(e => e.ResolvedOnDate),
            "updatedat" => query.SortDescending ? q.OrderByDescending(e => e.UpdatedAt) : q.OrderBy(e => e.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(e => e.CreatedAt) : q.OrderBy(e => e.CreatedAt),
            "filedondate" or "date" => query.SortDescending ? q.OrderByDescending(e => e.FiledOnDate ?? e.CreatedAt) : q.OrderBy(e => e.FiledOnDate ?? e.CreatedAt),
            _ => q.OrderByDescending(e => e.FiledOnDate ?? e.CreatedAt),
        };

        return ordered.ThenByDescending(e => e.Id);
    }

    private static IQueryable<EvictionCaseRow> ProjectRows(IQueryable<EvictionCase> query)
    {
        return query.Select(e => new EvictionCaseRow
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            LeaseId = e.LeaseId,
            LeaseNumber = e.Lease!.LeaseNumber,
            PropertyId = e.PropertyId,
            PropertyName = e.Property!.Name,
            UnitId = e.UnitId,
            UnitNumber = e.Unit!.UnitNumber,
            TenantId = e.TenantId,
            TenantName = (e.Tenant!.FirstName + " " + e.Tenant.LastName).Trim(),
            Status = e.Status,
            FiledOnDate = e.FiledOnDate,
            HearingDate = e.HearingDate,
            ResolvedOnDate = e.ResolvedOnDate,
            CourtName = e.CourtName,
            CaseNumber = e.CaseNumber,
            Resolution = e.Resolution,
            Notes = e.Notes,
            EventCount = e.Events.Count,
            LatestEventDate = e.Events
                .OrderByDescending(evt => evt.EventDate)
                .Select(evt => (DateTime?)evt.EventDate)
                .FirstOrDefault(),
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        });
    }

    private static EvictionCaseResponse ToResponse(EvictionCaseRow row)
    {
        return new EvictionCaseResponse
        {
            Id = row.Id,
            PortfolioId = row.PortfolioId,
            LeaseId = row.LeaseId,
            LeaseNumber = row.LeaseNumber,
            PropertyId = row.PropertyId,
            PropertyName = row.PropertyName,
            UnitId = row.UnitId,
            UnitNumber = row.UnitNumber,
            TenantId = row.TenantId,
            TenantName = row.TenantName,
            Status = row.Status,
            FiledOnDate = row.FiledOnDate,
            HearingDate = row.HearingDate,
            ResolvedOnDate = row.ResolvedOnDate,
            CourtName = row.CourtName,
            CaseNumber = row.CaseNumber,
            Resolution = row.Resolution,
            Notes = row.Notes,
            EventCount = row.EventCount,
            LatestEventDate = row.LatestEventDate,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }

    private static DateTime ResolveInitialEventDate(CreateEvictionCaseRequest request, DateTime now)
    {
        var date = request.Status switch
        {
            EvictionCaseStatus.HearingScheduled => request.HearingDate ?? request.FiledOnDate,
            _ => request.FiledOnDate,
        };
        return date?.ToUtc().Date ?? now.Date;
    }

    private static EvictionEventType? InitialEventForStatus(EvictionCaseStatus status) => status switch
    {
        EvictionCaseStatus.NoticeServed => EvictionEventType.NoticeServed,
        EvictionCaseStatus.Filed => EvictionEventType.Filed,
        EvictionCaseStatus.HearingScheduled => EvictionEventType.HearingScheduled,
        EvictionCaseStatus.Judgment => EvictionEventType.Judgment,
        EvictionCaseStatus.MoveOut => EvictionEventType.MoveOut,
        EvictionCaseStatus.Settled => EvictionEventType.Settlement,
        EvictionCaseStatus.Dismissed => EvictionEventType.Dismissal,
        _ => null,
    };

    private static void ApplyEventToCase(EvictionCase entity, EvictionCaseEvent evt, DateTime now)
    {
        switch (evt.EventType)
        {
            case EvictionEventType.NoticeServed:
                entity.Status = EvictionCaseStatus.NoticeServed;
                break;
            case EvictionEventType.Filed:
                entity.Status = EvictionCaseStatus.Filed;
                entity.FiledOnDate = evt.EventDate;
                break;
            case EvictionEventType.HearingScheduled:
                entity.Status = EvictionCaseStatus.HearingScheduled;
                entity.HearingDate = evt.EventDate;
                break;
            case EvictionEventType.Judgment:
                entity.Status = EvictionCaseStatus.Judgment;
                entity.ResolvedOnDate = evt.EventDate;
                entity.Resolution = Normalize(evt.Notes) ?? "Judgment";
                break;
            case EvictionEventType.MoveOut:
                entity.Status = EvictionCaseStatus.MoveOut;
                entity.ResolvedOnDate = evt.EventDate;
                entity.Resolution = Normalize(evt.Notes) ?? "Move-out completed";
                break;
            case EvictionEventType.Settlement:
                entity.Status = EvictionCaseStatus.Settled;
                entity.ResolvedOnDate = evt.EventDate;
                entity.Resolution = Normalize(evt.Notes) ?? "Settlement";
                break;
            case EvictionEventType.Dismissal:
                entity.Status = EvictionCaseStatus.Dismissed;
                entity.ResolvedOnDate = evt.EventDate;
                entity.Resolution = Normalize(evt.Notes) ?? "Dismissed";
                break;
            case EvictionEventType.PaymentPlan:
            case EvictionEventType.Note:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt.EventType), evt.EventType, "Unsupported eviction event type.");
        }

        entity.UpdatedAt = now;
    }

    private static void ApplyLeaseWorkflow(EvictionCase entity, Lease lease, DateTime now, DateTime workflowDate)
    {
        if (entity.Status is EvictionCaseStatus.NoticeServed or EvictionCaseStatus.Filed or EvictionCaseStatus.HearingScheduled)
        {
            if (lease.Status == LeaseStatus.Active)
            {
                lease.Status = LeaseStatus.NoticeGiven;
                lease.UpdatedAt = now;
            }
            return;
        }

        if (entity.Status == EvictionCaseStatus.MoveOut)
        {
            lease.Status = LeaseStatus.Terminated;
            if (lease.EndDate > workflowDate)
                lease.EndDate = workflowDate;
            lease.MoveOutDate = workflowDate;
            lease.UpdatedAt = now;

            if (lease.Unit is not null)
            {
                lease.Unit.Status = UnitStatus.Vacant;
                lease.Unit.UpdatedAt = now;
            }
        }
    }

    private async Task BroadcastCaseAndRelatedAsync(
        int portfolioId,
        int caseId,
        EvictionCaseResponse response,
        Lease lease,
        Unit? unit,
        CancellationToken ct)
    {
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, caseId, response, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, LeaseEntityType, lease.Id, LeaseResponse.FromEntity(lease), ct);
        if (unit is not null)
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, UnitEntityType, unit.Id, UnitResponse.FromEntity(unit), ct);
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private sealed class EvictionCaseRow
    {
        public int Id { get; init; }
        public int PortfolioId { get; init; }
        public int LeaseId { get; init; }
        public string? LeaseNumber { get; init; }
        public int PropertyId { get; init; }
        public string? PropertyName { get; init; }
        public int UnitId { get; init; }
        public string? UnitNumber { get; init; }
        public int TenantId { get; init; }
        public string? TenantName { get; init; }
        public EvictionCaseStatus Status { get; init; }
        public DateTime? FiledOnDate { get; init; }
        public DateTime? HearingDate { get; init; }
        public DateTime? ResolvedOnDate { get; init; }
        public string? CourtName { get; init; }
        public string? CaseNumber { get; init; }
        public string? Resolution { get; init; }
        public string? Notes { get; init; }
        public int EventCount { get; init; }
        public DateTime? LatestEventDate { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
