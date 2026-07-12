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
    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;

    public EvictionCaseService(RentalCommandDbContext db, IDataUpdateService dataUpdate, TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<EvictionCaseResponse>> ListAsync(int portfolioId, EvictionCaseListQuery query, CancellationToken ct = default)
        => (await ListPageAsync(portfolioId, query, ct)).Items;

    public async Task<EvictionCaseListResponse> ListPageAsync(int portfolioId, EvictionCaseListQuery query, CancellationToken ct = default)
    {
        var filtered = ApplyFilters(BaseQuery(portfolioId), query);
        var totalCount = await filtered.CountAsync(ct);
        var ordered = query.SortField switch
        {
            "relationshipnumber" => query.SortDescending ? filtered.OrderByDescending(e => e.LeaseManagement!.RelationshipNumber) : filtered.OrderBy(e => e.LeaseManagement!.RelationshipNumber),
            "propertyname" => query.SortDescending ? filtered.OrderByDescending(e => e.Property!.Name) : filtered.OrderBy(e => e.Property!.Name),
            "unitnumber" => query.SortDescending ? filtered.OrderByDescending(e => e.Unit!.UnitNumber) : filtered.OrderBy(e => e.Unit!.UnitNumber),
            "status" => query.SortDescending ? filtered.OrderByDescending(e => e.Status) : filtered.OrderBy(e => e.Status),
            "hearingdate" => query.SortDescending ? filtered.OrderByDescending(e => e.HearingDate) : filtered.OrderBy(e => e.HearingDate),
            "updatedat" => query.SortDescending ? filtered.OrderByDescending(e => e.UpdatedAt) : filtered.OrderBy(e => e.UpdatedAt),
            _ => query.SortDescending ? filtered.OrderByDescending(e => e.FiledOnDate).ThenByDescending(e => e.Id) : filtered.OrderBy(e => e.FiledOnDate).ThenBy(e => e.Id),
        };
        var items = await ProjectResponses(ordered, includeEvents: false)
            .Skip(query.NormalizedSkip).Take(query.NormalizedTake).ToListAsync(ct);
        return new EvictionCaseListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<EvictionCaseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        return await ProjectResponses(BaseQuery(portfolioId).Where(e => e.Id == id), includeEvents: true)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<EvictionCaseResponse?> CreateAsync(int portfolioId, CreateEvictionCaseRequest request, CancellationToken ct = default)
    {
        var context = await _db.LeaseManagements.AsNoTracking()
            .Where(m => m.Id == request.LeaseManagementId && m.PortfolioId == portfolioId)
            .Select(m => new
            {
                Management = m,
                AgreementValid = request.LeaseAgreementId == null || _db.LeaseAgreements.Any(a =>
                    a.Id == request.LeaseAgreementId && a.PortfolioId == portfolioId && a.LeaseManagementId == m.Id),
                RespondentCount = _db.LeaseManagementParties.Count(p =>
                    request.RespondentLeaseManagementPartyIds.Contains(p.Id) &&
                    p.PortfolioId == portfolioId && p.LeaseManagementId == m.Id),
            })
            .FirstOrDefaultAsync(ct);
        var respondentIds = request.RespondentLeaseManagementPartyIds.Distinct().ToArray();
        if (context is null || !context.AgreementValid || context.RespondentCount != respondentIds.Length)
            return null;

        var now = _timeProvider.UtcNow();
        var eventDate = request.FiledOnDate?.ToUtc().Date ?? now.Date;
        var entity = new EvictionCase
        {
            PortfolioId = portfolioId,
            LeaseManagementId = context.Management.Id,
            LeaseAgreementId = request.LeaseAgreementId,
            PropertyId = context.Management.PropertyId,
            UnitId = context.Management.UnitId,
            Status = request.Status,
            FiledOnDate = request.FiledOnDate?.ToUtc().Date ?? (request.Status >= EvictionCaseStatus.Filed ? eventDate : null),
            HearingDate = request.HearingDate?.ToUtc().Date,
            CourtName = Normalize(request.CourtName), CaseNumber = Normalize(request.CaseNumber), Notes = Normalize(request.Notes),
            CreatedAt = now, UpdatedAt = now,
        };
        foreach (var partyId in respondentIds)
            entity.Respondents.Add(new EvictionCaseRespondent { PortfolioId = portfolioId, LeaseManagementPartyId = partyId });
        var initialType = InitialEventForStatus(entity.Status);
        if (initialType.HasValue)
            entity.Events.Add(new EvictionCaseEvent { PortfolioId = portfolioId, EventType = initialType.Value, EventDate = eventDate, Notes = entity.Notes ?? "Case opened.", CreatedAt = now, UpdatedAt = now });
        _db.EvictionCases.Add(entity);
        await _db.SaveChangesAsync(ct);
        var response = await GetAsync(portfolioId, entity.Id, ct);
        if (response is null) return null;
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<EvictionCaseResponse?> UpdateAsync(int portfolioId, int id, UpdateEvictionCaseRequest request, CancellationToken ct = default)
    {
        var entity = await _db.EvictionCases.FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
        if (entity is null) return null;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.FiledOnDate.HasValue) entity.FiledOnDate = request.FiledOnDate.Value.ToUtc().Date;
        if (request.HearingDate.HasValue) entity.HearingDate = request.HearingDate.Value.ToUtc().Date;
        if (request.ResolvedOnDate.HasValue) entity.ResolvedOnDate = request.ResolvedOnDate.Value.ToUtc().Date;
        if (request.CourtName != null) entity.CourtName = Normalize(request.CourtName);
        if (request.CaseNumber != null) entity.CaseNumber = Normalize(request.CaseNumber);
        if (request.Resolution != null) entity.Resolution = Normalize(request.Resolution);
        if (request.Notes != null) entity.Notes = Normalize(request.Notes);
        entity.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);
        var response = await GetAsync(portfolioId, id, ct);
        if (response != null) await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, id, response, ct);
        return response;
    }

    public async Task<EvictionCaseResponse?> AddEventAsync(int portfolioId, int id, CreateEvictionCaseEventRequest request, CancellationToken ct = default)
    {
        var entity = await _db.EvictionCases.FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
        if (entity is null) return null;
        var now = _timeProvider.UtcNow();
        var evt = new EvictionCaseEvent { PortfolioId = portfolioId, EvictionCaseId = id, EventType = request.EventType, EventDate = request.EventDate.ToUtc().Date, Notes = Normalize(request.Notes), CreatedAt = now, UpdatedAt = now };
        _db.EvictionCaseEvents.Add(evt);
        ApplyEventToCase(entity, evt, now);
        await _db.SaveChangesAsync(ct);
        var response = await GetAsync(portfolioId, id, ct);
        if (response != null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EventEntityType, evt.Id, EvictionCaseEventResponse.FromEntity(evt), ct);
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, id, response, ct);
        }
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.EvictionCases.Include(e => e.Events).FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
        if (entity is null) return false;
        var now = _timeProvider.UtcNow();
        entity.DeletedAt = now; entity.UpdatedAt = now;
        foreach (var evt in entity.Events) { evt.DeletedAt = now; evt.UpdatedAt = now; }
        await _db.SaveChangesAsync(ct);
        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    private IQueryable<EvictionCase> BaseQuery(int portfolioId) => _db.EvictionCases.AsNoTracking()
        .Where(e => e.PortfolioId == portfolioId);

    private static IQueryable<EvictionCaseResponse> ProjectResponses(IQueryable<EvictionCase> query, bool includeEvents)
        => query.Select(e => new EvictionCaseResponse
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            LeaseManagementId = e.LeaseManagementId,
            RelationshipNumber = e.LeaseManagement!.RelationshipNumber,
            LeaseAgreementId = e.LeaseAgreementId,
            AgreementNumber = e.LeaseAgreement == null ? null : e.LeaseAgreement.AgreementNumber,
            PropertyId = e.PropertyId,
            PropertyName = e.Property!.Name,
            UnitId = e.UnitId,
            UnitNumber = e.Unit!.UnitNumber,
            Respondents = e.Respondents
                .OrderBy(r => r.LeaseManagementParty!.Tenant!.LastName)
                .ThenBy(r => r.LeaseManagementParty!.Tenant!.FirstName)
                .Select(r => new EvictionCaseRespondentResponse
                {
                    LeaseManagementPartyId = r.LeaseManagementPartyId,
                    TenantId = r.LeaseManagementParty!.TenantId,
                    TenantName = (r.LeaseManagementParty.Tenant!.FirstName + " " + r.LeaseManagementParty.Tenant.LastName).Trim(),
                }).ToList(),
            Status = e.Status,
            FiledOnDate = e.FiledOnDate,
            HearingDate = e.HearingDate,
            ResolvedOnDate = e.ResolvedOnDate,
            CourtName = e.CourtName,
            CaseNumber = e.CaseNumber,
            Resolution = e.Resolution,
            Notes = e.Notes,
            EventCount = e.Events.Count,
            LatestEventDate = e.Events.Select(evt => (DateTime?)evt.EventDate).Max(),
            Events = includeEvents
                ? e.Events.OrderBy(evt => evt.EventDate).ThenBy(evt => evt.Id)
                    .Select(evt => new EvictionCaseEventResponse
                    {
                        Id = evt.Id, PortfolioId = evt.PortfolioId, EvictionCaseId = evt.EvictionCaseId,
                        EventType = evt.EventType, EventDate = evt.EventDate, Notes = evt.Notes,
                        CreatedAt = evt.CreatedAt, UpdatedAt = evt.UpdatedAt,
                    }).ToList()
                : new List<EvictionCaseEventResponse>(),
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        });

    private static IQueryable<EvictionCase> ApplyFilters(IQueryable<EvictionCase> q, EvictionCaseListQuery query)
    {
        if (query.LeaseManagementId.HasValue) q = q.Where(e => e.LeaseManagementId == query.LeaseManagementId.Value);
        if (query.PropertyId.HasValue) q = q.Where(e => e.PropertyId == query.PropertyId.Value);
        if (query.LeaseManagementPartyId.HasValue) q = q.Where(e => e.Respondents.Any(r => r.LeaseManagementPartyId == query.LeaseManagementPartyId.Value));
        if (query.Status.HasValue) q = q.Where(e => e.Status == query.Status.Value);
        var (from, to) = ListDateRange.UtcDay(query.From, query.To);
        if (from is { } fromUtc) q = q.Where(e => (e.FiledOnDate ?? e.CreatedAt) >= fromUtc);
        if (to is { } toUtcExclusive) q = q.Where(e => (e.FiledOnDate ?? e.CreatedAt) < toUtcExclusive);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(e => EF.Functions.ILike(e.LeaseManagement!.RelationshipNumber, $"%{term}%") || (e.CaseNumber != null && EF.Functions.ILike(e.CaseNumber, $"%{term}%")) || e.Respondents.Any(r => EF.Functions.ILike(r.LeaseManagementParty!.Tenant!.FirstName, $"%{term}%") || EF.Functions.ILike(r.LeaseManagementParty!.Tenant!.LastName, $"%{term}%")));
        }
        return q;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
            case EvictionEventType.NoticeServed: entity.Status = EvictionCaseStatus.NoticeServed; break;
            case EvictionEventType.Filed: entity.Status = EvictionCaseStatus.Filed; entity.FiledOnDate ??= evt.EventDate; break;
            case EvictionEventType.HearingScheduled: entity.Status = EvictionCaseStatus.HearingScheduled; entity.HearingDate = evt.EventDate; break;
            case EvictionEventType.Judgment: entity.Status = EvictionCaseStatus.Judgment; entity.ResolvedOnDate ??= evt.EventDate; entity.Resolution = Normalize(evt.Notes) ?? "Judgment"; break;
            case EvictionEventType.MoveOut: entity.Status = EvictionCaseStatus.MoveOut; entity.ResolvedOnDate ??= evt.EventDate; entity.Resolution = Normalize(evt.Notes) ?? "Move-out completed"; break;
            case EvictionEventType.Settlement: entity.Status = EvictionCaseStatus.Settled; entity.ResolvedOnDate ??= evt.EventDate; entity.Resolution = Normalize(evt.Notes) ?? "Settlement"; break;
            case EvictionEventType.Dismissal: entity.Status = EvictionCaseStatus.Dismissed; entity.ResolvedOnDate ??= evt.EventDate; entity.Resolution = Normalize(evt.Notes) ?? "Dismissed"; break;
            case EvictionEventType.PaymentPlan:
            case EvictionEventType.Note:
                break;
        }
        entity.UpdatedAt = now;
    }
}
