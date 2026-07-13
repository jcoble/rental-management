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

/// <inheritdoc cref="IRecurringExpenseService"/>
public class RecurringExpenseService : IRecurringExpenseService
{
    private const string EntityType = "RecurringExpense";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork? _atomic;

    public RecurringExpenseService(RentalCommandDbContext db, IDataUpdateService dataUpdate,
        TimeProvider timeProvider, IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    public async Task<IReadOnlyList<RecurringExpenseResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, propertyId, query, ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<RecurringExpenseResponse>> ListAsync(
        WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, propertyId, query, ct);
        return page.Items;
    }

    public async Task<RecurringExpenseListResponse> ListPageAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(portfolioId, propertyId, query);
        return await BuildPageAsync(filtered, query, ct);
    }

    public Task<RecurringExpenseListResponse> ListPageAsync(
        WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(
            _db.RecurringExpenses.AsNoTracking().WhereMoneyAuthorized(
                _db, scope, CapabilityKeys.MoneyBalancesRead, _timeProvider.UtcNow()),
            scope.PortfolioId, propertyId, query);
        return BuildPageAsync(filtered, query, ct);
    }

    private static async Task<RecurringExpenseListResponse> BuildPageAsync(
        IQueryable<RecurringExpense> filtered, ListQuery query, CancellationToken ct)
    {
        var totalCount = await filtered.CountAsync(ct);

        var items = await ApplySort(filtered, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(t => new { Template = t, PropertyName = t.Property != null ? t.Property.Name : null })
            .ToListAsync(ct);

        return new RecurringExpenseListResponse
        {
            Items = items.Select(x =>
            {
                var r = RecurringExpenseResponse.FromEntity(x.Template);
                r.PropertyName = x.PropertyName;
                return r;
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private IQueryable<RecurringExpense> BuildListQuery(int portfolioId, int? propertyId, ListQuery query)
        => BuildListQuery(_db.RecurringExpenses.AsNoTracking(), portfolioId, propertyId, query);

    private static IQueryable<RecurringExpense> BuildListQuery(
        IQueryable<RecurringExpense> q, int portfolioId, int? propertyId, ListQuery query)
    {
        q = q.Where(t => t.PortfolioId == portfolioId);

        if (propertyId.HasValue)
            q = q.Where(t => t.PropertyId == propertyId.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(t => EF.Functions.ILike(t.Description, $"%{term}%"));
        }

        var (from, to) = ListDateRange.UtcDay(query.From, query.To);
        if (from is { } fromUtc)
            q = q.Where(t => t.NextRunDate >= fromUtc);
        if (to is { } toUtcExclusive)
            q = q.Where(t => t.NextRunDate < toUtcExclusive);

        return q;
    }

    private static IQueryable<RecurringExpense> ApplySort(IQueryable<RecurringExpense> q, ListQuery query)
    {
        var ordered = query.SortField switch
        {
            "description" => query.SortDescending ? q.OrderByDescending(t => t.Description) : q.OrderBy(t => t.Description),
            "amount" => query.SortDescending ? q.OrderByDescending(t => t.Amount) : q.OrderBy(t => t.Amount),
            "category" => query.SortDescending ? q.OrderByDescending(t => t.Category) : q.OrderBy(t => t.Category),
            "frequency" => query.SortDescending ? q.OrderByDescending(t => t.Frequency) : q.OrderBy(t => t.Frequency),
            "startdate" => query.SortDescending ? q.OrderByDescending(t => t.StartDate) : q.OrderBy(t => t.StartDate),
            "nextrundate" => query.SortDescending ? q.OrderByDescending(t => t.NextRunDate) : q.OrderBy(t => t.NextRunDate),
            "updatedat" => query.SortDescending ? q.OrderByDescending(t => t.UpdatedAt) : q.OrderBy(t => t.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
            _ => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
        };

        return ordered.ThenBy(t => t.Id);
    }

    public Task<RecurringExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default) =>
        GetAsync(_db.RecurringExpenses.AsNoTracking(), portfolioId, id, ct);

    public Task<RecurringExpenseResponse?> GetAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default) =>
        GetAsync(
            _db.RecurringExpenses.AsNoTracking().WhereMoneyAuthorized(
                _db, scope, CapabilityKeys.MoneyBalancesRead, _timeProvider.UtcNow()),
            scope.PortfolioId, id, ct);

    private static async Task<RecurringExpenseResponse?> GetAsync(
        IQueryable<RecurringExpense> expenses, int portfolioId, int id, CancellationToken ct)
    {
        var entity = await expenses
            .Include(t => t.Property)
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);

        return entity == null ? null : RecurringExpenseResponse.FromEntity(entity);
    }

    public async Task<RecurringExpenseResponse?> CreateAsync(int portfolioId, CreateRecurringExpenseRequest request, CancellationToken ct = default)
    {
        // IDOR guards: any supplied property/unit must belong to this portfolio.
        if (request.PropertyId.HasValue &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId.Value, ct))
            return null;

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, request.PropertyId, ct))
            return null;

        var now = _timeProvider.UtcNow();
        var start = request.StartDate.ToUtc();
        var entity = new RecurringExpense
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            Category = request.Category,
            Description = request.Description,
            Amount = request.Amount,
            Frequency = request.Frequency,
            StartDate = start,
            NextRunDate = (request.NextRunDate?.ToUtc()) ?? start,
            Active = request.Active,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.RecurringExpenses.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = await BuildResponseAsync(entity, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<RecurringExpenseResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateRecurringExpenseRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.RecurringExpense, AtomicMoneyOperation.Create, 0, idempotencyKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        if (!outcome.Value.Found) return null;
        var response = await GetAsync(scope.PortfolioId, outcome.Value.EntityId, ct);
        if (response is not null)
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    public async Task<RecurringExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateRecurringExpenseRequest request, CancellationToken ct = default)
    {
        var entity = await _db.RecurringExpenses
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (entity == null)
            return null;

        // Validate FK changes in-portfolio. Use the effective property id for the unit check.
        if (request.PropertyId.HasValue &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId.Value, ct))
            return null;

        var effectivePropertyId = request.PropertyId ?? entity.PropertyId;
        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, effectivePropertyId, ct))
            return null;

        if (request.PropertyId.HasValue) entity.PropertyId = request.PropertyId;
        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.Category.HasValue) entity.Category = request.Category.Value;
        if (request.Description != null) entity.Description = request.Description;
        if (request.Amount.HasValue) entity.Amount = request.Amount.Value;
        if (request.Frequency.HasValue) entity.Frequency = request.Frequency.Value;
        if (request.StartDate.HasValue) entity.StartDate = request.StartDate.Value.ToUtc();
        if (request.NextRunDate.HasValue) entity.NextRunDate = request.NextRunDate.Value.ToUtc();
        if (request.Active.HasValue) entity.Active = request.Active.Value;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = await BuildResponseAsync(entity, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<RecurringExpenseResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateRecurringExpenseRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        if (await GetAsync(scope, id, ct) is null)
            return null;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.RecurringExpense, AtomicMoneyOperation.Update, id, idempotencyKey, request);
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
        var entity = await _db.RecurringExpenses
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (entity == null)
            return false;

        entity.DeletedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default)
    {
        var visible = await GetAsync(scope, id, ct) is not null;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.RecurringExpense, AtomicMoneyOperation.Delete, id, idempotencyKey, new object());
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
        "Scoped recurring-expense mutations require the atomic persistence kernel.");

    private async Task<bool> HasTargetCapabilityAsync(
        WorkspaceReadScope scope, int? propertyId, int? unitId,
        string capabilityKey, CancellationToken ct)
    {
        var now = _timeProvider.UtcNow();
        if (propertyId is null && unitId is null)
        {
            return await _db.AuthorizedAllPropertyAssignments(
                    scope, capabilityKey, CapabilityAuthorizationTargetKind.Property, now)
                .AnyAsync(ct);
        }

        return await _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, capabilityKey, now)
            .AnyAsync(property =>
                (propertyId == null || property.Id == propertyId) &&
                (unitId == null || _db.Units.Any(unit =>
                    unit.Id == unitId && unit.PortfolioId == scope.PortfolioId &&
                    unit.PropertyId == property.Id)), ct);
    }

    private async Task<RecurringExpenseResponse> BuildResponseAsync(RecurringExpense entity, CancellationToken ct)
    {
        var response = RecurringExpenseResponse.FromEntity(entity);
        if (entity.PropertyId.HasValue)
        {
            response.PropertyName = await _db.Properties
                .AsNoTracking()
                .Where(p => p.Id == entity.PropertyId.Value)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(ct);
        }
        return response;
    }
}
