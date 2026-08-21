using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Money;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IRecurringExpenseService"/>
public class RecurringExpenseService : IRecurringExpenseService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestWriteExecutor _writes;

    public RecurringExpenseService(
        RentalCommandDbContext db, TimeProvider timeProvider, IRequestWriteExecutor writes)
    {
        _db = db;
        _timeProvider = timeProvider;
        _writes = writes;
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

        var items = await MoneyResponseProjection.RecurringExpenses(ApplySort(filtered, query))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new RecurringExpenseListResponse
        {
            Items = items,
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

    private static Task<RecurringExpenseResponse?> GetAsync(
        IQueryable<RecurringExpense> expenses, int portfolioId, int id, CancellationToken ct)
        => MoneyResponseProjection.RecurringExpenses(
                expenses.Where(expense => expense.Id == id && expense.PortfolioId == portfolioId))
            .FirstOrDefaultAsync(ct);

    public async Task<RecurringExpenseResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateRecurringExpenseRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.RecurringExpense, AtomicMoneyOperation.Create, 0, idempotencyKey, request,
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        if (!outcome.Value.Found) return null;
        return ReadSnapshot<RecurringExpenseResponse>(outcome.Value);
    }

    public async Task<RecurringExpenseResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateRecurringExpenseRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.RecurringExpense, AtomicMoneyOperation.Update, id, idempotencyKey, request,
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        if (!outcome.Value.Found) return null;
        return ReadSnapshot<RecurringExpenseResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.RecurringExpense, AtomicMoneyOperation.Delete, id, idempotencyKey, new object(),
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        return outcome.Value.Found;
    }

    private Task<AtomicCommandOutcome<AtomicMoneyMutationResult>> ExecuteAsync(
        AtomicMoneyMutationCommand command, CancellationToken ct) =>
        _writes.ExecuteAsync(
            AtomicMoneyMutation.Identity(command).IdempotencyKey,
            AtomicMoneyMutation.Write(command, _db), ct);

    private static TResponse ReadSnapshot<TResponse>(AtomicMoneyMutationResult result) where TResponse : class =>
        result.ResponseJson is { Length: > 0 } json
            ? System.Text.Json.JsonSerializer.Deserialize<TResponse>(json)
                ?? throw new AtomicReceiptInvariantException("The money receipt snapshot is invalid.")
            : throw new AtomicReceiptInvariantException("The money receipt snapshot is missing.");
}
