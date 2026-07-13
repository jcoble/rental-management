using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOwnerDistributionService"/>
public class OwnerDistributionService : IOwnerDistributionService
{
    private const string EntityType = "OwnerDistribution";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork? _atomic;

    public OwnerDistributionService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    public async Task<IReadOnlyList<OwnerDistributionResponse>> ListAsync(
        int portfolioId, OwnerDistributionListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, query, ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<OwnerDistributionResponse>> ListAsync(
        WorkspaceReadScope scope, OwnerDistributionListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, query, ct);
        return page.Items;
    }

    public async Task<OwnerDistributionListResponse> ListPageAsync(
        int portfolioId, OwnerDistributionListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(portfolioId, query);
        return await BuildPageAsync(filtered, query, ct);
    }

    public Task<OwnerDistributionListResponse> ListPageAsync(
        WorkspaceReadScope scope, OwnerDistributionListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(
            _db.OwnerDistributions.AsNoTracking().WhereMoneyAuthorized(
                _db, scope, CapabilityKeys.MoneyOwnerReportsRead, _timeProvider.UtcNow()),
            scope.PortfolioId, query);
        return BuildPageAsync(filtered, query, ct);
    }

    private async Task<OwnerDistributionListResponse> BuildPageAsync(
        IQueryable<OwnerDistribution> filtered, OwnerDistributionListQuery query, CancellationToken ct)
    {
        var totalCount = await filtered.CountAsync(ct);

        var items = await ProjectResponse(ApplySort(filtered, query))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new OwnerDistributionListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<IReadOnlyList<OwnerDistributionResponse>> ListForOwnerYearAsync(
        int portfolioId, int ownerEntityId, int year, CancellationToken ct = default)
    {
        var query = new OwnerDistributionListQuery
        {
            OwnerEntityId = ownerEntityId,
            Year = year,
            Sort = "-date",
            Take = ListQuery.MaxTake,
        };

        return await ProjectResponse(ApplySort(BuildListQuery(portfolioId, query), query))
            .ToListAsync(ct);
    }

    public async Task<decimal> SumForOwnerYearAsync(
        int portfolioId, int ownerEntityId, int year, CancellationToken ct = default)
    {
        var (start, end) = YearRange(year);

        return await _db.OwnerDistributions
            .AsNoTracking()
            .Where(d =>
                d.PortfolioId == portfolioId &&
                d.OwnerEntityId == ownerEntityId &&
                d.Date >= start &&
                d.Date < end)
            .SumAsync(d => (decimal?)d.Amount, ct) ?? 0m;
    }

    public Task<OwnerDistributionResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default) =>
        GetAsync(_db.OwnerDistributions.AsNoTracking(), portfolioId, id, ct);

    public Task<OwnerDistributionResponse?> GetAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default) =>
        GetAsync(
            _db.OwnerDistributions.AsNoTracking().WhereMoneyAuthorized(
                _db, scope, CapabilityKeys.MoneyOwnerReportsRead, _timeProvider.UtcNow()),
            scope.PortfolioId, id, ct);

    private async Task<OwnerDistributionResponse?> GetAsync(
        IQueryable<OwnerDistribution> distributions, int portfolioId, int id, CancellationToken ct)
    {
        return await ProjectResponse(distributions.Where(d => d.Id == id && d.PortfolioId == portfolioId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<OwnerDistributionResponse?> CreateAsync(
        int portfolioId, CreateOwnerDistributionRequest request, CancellationToken ct = default)
    {
        if (!await _db.EnsureOwnerEntityInPortfolioAsync(portfolioId, request.OwnerEntityId, ct))
            return null;

        if (request.PropertyId is { } propertyId &&
            !await PropertyBelongsToOwnerAsync(portfolioId, propertyId, request.OwnerEntityId, ct))
            return null;

        var now = _timeProvider.UtcNow();
        var entity = new OwnerDistribution
        {
            PortfolioId = portfolioId,
            OwnerEntityId = request.OwnerEntityId,
            PropertyId = request.PropertyId,
            Date = request.Date.ToUtc(),
            Amount = request.Amount,
            Method = request.Method,
            Memo = request.Memo,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.OwnerDistributions.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? ProjectResponse(entity, ownerName: "", propertyName: null);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<OwnerDistributionResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Create, 0, idempotencyKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        if (!outcome.Value.Found) return null;
        var response = await GetAsync(scope.PortfolioId, outcome.Value.EntityId, ct);
        if (response is not null)
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    public async Task<OwnerDistributionResponse?> UpdateAsync(
        int portfolioId, int id, UpdateOwnerDistributionRequest request, CancellationToken ct = default)
    {
        var entity = await _db.OwnerDistributions
            .FirstOrDefaultAsync(d => d.Id == id && d.PortfolioId == portfolioId, ct);
        if (entity is null)
            return null;

        var ownerEntityId = request.OwnerEntityId ?? entity.OwnerEntityId;
        var propertyId = entity.PropertyId;
        if (request.ClearProperty == true)
            propertyId = null;
        if (request.PropertyId.HasValue)
            propertyId = request.PropertyId.Value;

        if (request.OwnerEntityId.HasValue &&
            !await _db.EnsureOwnerEntityInPortfolioAsync(portfolioId, request.OwnerEntityId.Value, ct))
            return null;

        if (propertyId.HasValue &&
            !await PropertyBelongsToOwnerAsync(portfolioId, propertyId.Value, ownerEntityId, ct))
            return null;

        entity.OwnerEntityId = ownerEntityId;
        entity.PropertyId = propertyId;
        if (request.Date.HasValue) entity.Date = request.Date.Value.ToUtc();
        if (request.Amount.HasValue) entity.Amount = request.Amount.Value;
        if (request.Method.HasValue) entity.Method = request.Method.Value;
        if (request.Memo != null) entity.Memo = request.Memo;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? ProjectResponse(entity, ownerName: "", propertyName: null);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<OwnerDistributionResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        if (await GetAsync(scope, id, ct) is null)
            return null;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Update, id, idempotencyKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        if (!outcome.Value.Found) return null;
        var response = await GetAsync(scope.PortfolioId, id, ct);
        if (response is not null)
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.OwnerDistributions
            .FirstOrDefaultAsync(d => d.Id == id && d.PortfolioId == portfolioId, ct);
        if (entity is null)
            return false;

        var now = _timeProvider.UtcNow();
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default)
    {
        var visible = await GetAsync(scope, id, ct) is not null;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Delete, id, idempotencyKey, new object());
        AtomicCommandOutcome<AtomicMoneyMutationResult> outcome;
        try
        {
            outcome = await Atomic.ExecuteAsync(
                AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        }
        catch (UnauthorizedAccessException) when (!visible)
        {
            return false;
        }
        if (outcome.Value.Applied)
            await _dataUpdate.BroadcastEntityDeleteAsync(scope.PortfolioId, EntityType, id, ct);
        return visible || outcome.Disposition == AtomicCommandDisposition.Replayed
            ? outcome.Value.Found
            : false;
    }

    private IAtomicUnitOfWork Atomic => _atomic ?? throw new InvalidOperationException(
        "Scoped owner-distribution mutations require the atomic persistence kernel.");

    private async Task DemandDisbursementAuthorityAsync(WorkspaceReadScope scope, CancellationToken ct)
    {
        var allowed = await _db.AuthorizedAllPropertyAssignments(
                scope, CapabilityKeys.MoneyDisbursementsManage,
                CapabilityAuthorizationTargetKind.Workspace, _timeProvider.UtcNow())
            .AnyAsync(ct);
        if (!allowed)
            throw new UnauthorizedAccessException(
                "Owner distributions require workspace payout authority.");
    }

    private IQueryable<OwnerDistribution> BuildListQuery(int portfolioId, OwnerDistributionListQuery query)
        => BuildListQuery(_db.OwnerDistributions.AsNoTracking(), portfolioId, query);

    private static IQueryable<OwnerDistribution> BuildListQuery(
        IQueryable<OwnerDistribution> q, int portfolioId, OwnerDistributionListQuery query)
    {
        q = q.Where(d => d.PortfolioId == portfolioId);

        if (query.OwnerEntityId.HasValue)
            q = q.Where(d => d.OwnerEntityId == query.OwnerEntityId.Value);

        if (query.PropertyId.HasValue)
            q = q.Where(d => d.PropertyId == query.PropertyId.Value);

        if (query.Year.HasValue)
        {
            var (start, end) = YearRange(query.Year.Value);
            q = q.Where(d => d.Date >= start && d.Date < end);
        }

        var (from, to) = ListDateRange.UtcDay(query.From, query.To);
        if (from is { } fromUtc)
            q = q.Where(d => d.Date >= fromUtc);
        if (to is { } toUtcExclusive)
            q = q.Where(d => d.Date < toUtcExclusive);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(d =>
                (d.Memo != null && EF.Functions.ILike(d.Memo, $"%{term}%")) ||
                EF.Functions.ILike(d.OwnerEntity!.Name, $"%{term}%") ||
                (d.Property != null && EF.Functions.ILike(d.Property.Name, $"%{term}%")));
        }

        return q;
    }

    private static IQueryable<OwnerDistribution> ApplySort(
        IQueryable<OwnerDistribution> q, OwnerDistributionListQuery query)
    {
        var ordered = query.SortField switch
        {
            "amount" => query.SortDescending ? q.OrderByDescending(d => d.Amount) : q.OrderBy(d => d.Amount),
            "owner" or "ownername" => query.SortDescending
                ? q.OrderByDescending(d => d.OwnerEntity!.Name)
                : q.OrderBy(d => d.OwnerEntity!.Name),
            "property" or "propertyname" => query.SortDescending
                ? q.OrderByDescending(d => d.Property!.Name)
                : q.OrderBy(d => d.Property!.Name),
            "method" => query.SortDescending ? q.OrderByDescending(d => d.Method) : q.OrderBy(d => d.Method),
            "updatedat" => query.SortDescending ? q.OrderByDescending(d => d.UpdatedAt) : q.OrderBy(d => d.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(d => d.CreatedAt) : q.OrderBy(d => d.CreatedAt),
            "date" => query.SortDescending ? q.OrderByDescending(d => d.Date) : q.OrderBy(d => d.Date),
            _ => q.OrderByDescending(d => d.Date),
        };

        return ordered.ThenByDescending(d => d.Id);
    }

    private IQueryable<OwnerDistributionResponse> ProjectResponse(IQueryable<OwnerDistribution> query)
    {
        return query.Select(d => new OwnerDistributionResponse
        {
            Id = d.Id,
            PortfolioId = d.PortfolioId,
            OwnerEntityId = d.OwnerEntityId,
            OwnerName = d.OwnerEntity!.Name,
            PropertyId = d.PropertyId,
            PropertyName = d.Property == null ? null : d.Property.Name,
            Date = d.Date,
            Amount = d.Amount,
            Method = d.Method,
            Memo = d.Memo,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
        });
    }

    private static OwnerDistributionResponse ProjectResponse(
        OwnerDistribution entity, string ownerName, string? propertyName)
    {
        return new OwnerDistributionResponse
        {
            Id = entity.Id,
            PortfolioId = entity.PortfolioId,
            OwnerEntityId = entity.OwnerEntityId,
            OwnerName = ownerName,
            PropertyId = entity.PropertyId,
            PropertyName = propertyName,
            Date = entity.Date,
            Amount = entity.Amount,
            Method = entity.Method,
            Memo = entity.Memo,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    private Task<bool> PropertyBelongsToOwnerAsync(
        int portfolioId, int propertyId, int ownerEntityId, CancellationToken ct)
    {
        return _db.Properties.AnyAsync(p =>
            p.Id == propertyId &&
            p.PortfolioId == portfolioId &&
            p.OwnerEntityId == ownerEntityId,
            ct);
    }

    private static (DateTime Start, DateTime End) YearRange(int year)
    {
        return (
            new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
