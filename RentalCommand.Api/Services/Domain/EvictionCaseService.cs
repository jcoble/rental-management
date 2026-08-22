using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Operations;

namespace RentalCommand.Api.Services.Domain;

public class EvictionCaseService : IEvictionCaseService
{
    private static readonly string[] ReadCapabilities = [CapabilityKeys.RentalsRead];
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestWriteExecutor? _writes;

    public EvictionCaseService(RentalCommandDbContext db, IDataUpdateService dataUpdate,
        TimeProvider timeProvider, IRequestWriteExecutor? writes = null)
    {
        _db = db;
        _timeProvider = timeProvider;
        _writes = writes;
    }

    public async Task<IReadOnlyList<EvictionCaseResponse>> ListAsync(int portfolioId, EvictionCaseListQuery query, CancellationToken ct = default)
        => (await ListPageAsync(portfolioId, query, ct)).Items;

    public async Task<EvictionCaseListResponse> ListPageAsync(int portfolioId, EvictionCaseListQuery query, CancellationToken ct = default)
        => await ListPageFromQueryAsync(BaseQuery(portfolioId), query, ct);

    public async Task<IReadOnlyList<EvictionCaseResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, EvictionCaseListQuery query, CancellationToken ct = default)
        => (await ListPageAuthorizedAsync(scope, query, ct)).Items;

    public Task<EvictionCaseListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, EvictionCaseListQuery query, CancellationToken ct = default)
        => ListPageFromQueryAsync(AuthorizedBaseQuery(scope, ReadCapabilities), query, ct);

    private async Task<EvictionCaseListResponse> ListPageFromQueryAsync(
        IQueryable<EvictionCase> baseQuery,
        EvictionCaseListQuery query,
        CancellationToken ct)
    {
        var filtered = ApplyFilters(baseQuery, query);
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

    public async Task<EvictionCaseResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default)
    {
        return await ProjectResponses(
                AuthorizedBaseQuery(scope, ReadCapabilities).Where(e => e.Id == id),
                includeEvents: true)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<EvictionCaseResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope, CreateEvictionCaseRequest request, string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new CreateEvictionCaseCommand(
            scope.PortfolioId, Actor(scope), request.LeaseManagementId, request.LeaseAgreementId,
            request.RespondentLeaseManagementPartyIds.ToArray(), request.Status,
            request.FiledOnDate?.ToUtc(), request.HearingDate?.ToUtc(), request.CourtName,
            request.CaseNumber, request.Notes, _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            EvictionCrudWriteSupport.IdempotencyKey(idempotencyKey),
            EvictionCrudWriteSupport.Write(command, CreateEvictionCaseAsync, AuthorizeReplayAsync), ct);
        return Response(outcome.Value);
    }

    public async Task<EvictionCaseResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope, int id, UpdateEvictionCaseRequest request,
        string idempotencyKey, CancellationToken ct = default)
    {
        var command = new UpdateEvictionCaseCommand(
            scope.PortfolioId, Actor(scope), id, request.Status, request.FiledOnDate?.ToUtc(),
            request.HearingDate?.ToUtc(), request.ResolvedOnDate?.ToUtc(), request.CourtName,
            request.CaseNumber, request.Resolution, request.Notes, _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            EvictionCrudWriteSupport.IdempotencyKey(idempotencyKey),
            EvictionCrudWriteSupport.Write(command, UpdateEvictionCaseAsync, AuthorizeReplayAsync), ct);
        return Response(outcome.Value);
    }

    public async Task<EvictionCaseResponse?> AddEventAuthorizedAsync(
        WorkspaceReadScope scope, int id, CreateEvictionCaseEventRequest request,
        string idempotencyKey, CancellationToken ct = default)
    {
        var command = new AddEvictionCaseEventCommand(
            scope.PortfolioId, Actor(scope), id, request.EventType, request.EventDate.ToUtc(),
            request.Notes, _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            EvictionCrudWriteSupport.IdempotencyKey(idempotencyKey),
            EvictionCrudWriteSupport.Write(command, AddEvictionCaseEventAsync, AuthorizeReplayAsync), ct);
        return Response(outcome.Value);
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default)
    {
        var command = new DeleteEvictionCaseCommand(
            scope.PortfolioId, Actor(scope), id, _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await RequireWrites().ExecuteAsync(
            EvictionCrudWriteSupport.IdempotencyKey(idempotencyKey),
            EvictionCrudWriteSupport.Write(command, DeleteEvictionCaseAsync, AuthorizeReplayAsync), ct);
        return outcome.Value.Outcome == OperationMutationOutcome.Applied;
    }

    private Task<OperationMutationResult> CreateEvictionCaseAsync(
        CreateEvictionCaseCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        CreateEvictionCaseRule.ExecuteAsync(_db, command, context, ct);

    private Task<OperationMutationResult> UpdateEvictionCaseAsync(
        UpdateEvictionCaseCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        UpdateEvictionCaseRule.ExecuteAsync(_db, command, context, ct);

    private Task<OperationMutationResult> AddEvictionCaseEventAsync(
        AddEvictionCaseEventCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        AddEvictionCaseEventRule.ExecuteAsync(_db, command, context, ct);

    private Task<OperationMutationResult> DeleteEvictionCaseAsync(
        DeleteEvictionCaseCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        DeleteEvictionCaseRule.ExecuteAsync(_db, command, context, ct);

    private Task AuthorizeReplayAsync(
        CreateEvictionCaseCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        CreateEvictionCaseRule.AuthorizeAsync(_db, command, context, ct);

    private Task AuthorizeReplayAsync(
        UpdateEvictionCaseCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        UpdateEvictionCaseRule.AuthorizeAsync(_db, command, context, ct);

    private Task AuthorizeReplayAsync(
        AddEvictionCaseEventCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        AddEvictionCaseEventRule.AuthorizeAsync(_db, command, context, ct);

    private Task AuthorizeReplayAsync(
        DeleteEvictionCaseCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        DeleteEvictionCaseRule.AuthorizeAsync(_db, command, context, ct);

    private IRequestWriteExecutor RequireWrites() => _writes ?? throw new InvalidOperationException(
        "The shared request write executor is required for eviction case changes.");

    private static StaffOperationActor Actor(WorkspaceReadScope scope) => new(
        scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private static EvictionCaseResponse? Response(OperationMutationResult result) =>
        result.Outcome == OperationMutationOutcome.NotFound || result.ResponseJson is null
            ? null
            : JsonSerializer.Deserialize<EvictionCaseResponse>(result.ResponseJson);

    private IQueryable<EvictionCase> BaseQuery(int portfolioId) => _db.EvictionCases.AsNoTracking()
        .Where(e => e.PortfolioId == portfolioId);

    private IQueryable<EvictionCase> AuthorizedBaseQuery(
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilities,
        bool tracking = false)
    {
        var authorizedProperties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(_db, scope, capabilities, _timeProvider.UtcNow());
        var cases = tracking ? _db.EvictionCases : _db.EvictionCases.AsNoTracking();
        return cases.Where(eviction =>
            eviction.PortfolioId == scope.PortfolioId &&
            authorizedProperties.Any(property =>
                property.Id == eviction.PropertyId &&
                property.PortfolioId == eviction.PortfolioId));
    }

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

}
