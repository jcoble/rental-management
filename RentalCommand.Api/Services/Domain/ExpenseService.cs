using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Money;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IExpenseService"/>
public class ExpenseService : IExpenseService
{
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestWriteExecutor _writes;

    public ExpenseService(RentalCommandDbContext db, IFileStorage files,
        TimeProvider timeProvider, IRequestWriteExecutor writes)
    {
        _db = db;
        _files = files;
        _timeProvider = timeProvider;
        _writes = writes;
    }

    public async Task<IReadOnlyList<ExpenseResponse>> ListAsync(int portfolioId, int? propertyId, int? unitId, int? workOrderId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, propertyId, unitId, workOrderId, workOrderLinkedOnly: false, query, ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<ExpenseResponse>> ListAsync(
        WorkspaceReadScope scope, int? propertyId, int? unitId, int? workOrderId,
        ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(
            scope, propertyId, unitId, workOrderId, workOrderLinkedOnly: false, query, ct);
        return page.Items;
    }

    public async Task<ExpenseListResponse> ListPageAsync(
        int portfolioId,
        int? propertyId,
        int? unitId,
        int? workOrderId,
        bool workOrderLinkedOnly,
        ListQuery query,
        CancellationToken ct = default)
    {
        var filtered = BuildListQuery(portfolioId, propertyId, unitId, workOrderId, workOrderLinkedOnly, query);
        return await BuildPageAsync(filtered, query, ct);
    }

    public Task<ExpenseListResponse> ListPageAsync(
        WorkspaceReadScope scope,
        int? propertyId,
        int? unitId,
        int? workOrderId,
        bool workOrderLinkedOnly,
        ListQuery query,
        CancellationToken ct = default)
    {
        var filtered = BuildListQuery(
            _db.Expenses.AsNoTracking().WhereMoneyAuthorized(
                _db, scope, CapabilityKeys.MoneyBalancesRead, _timeProvider.UtcNow()),
            scope.PortfolioId, propertyId, unitId, workOrderId, workOrderLinkedOnly, query);
        return BuildPageAsync(filtered, query, ct);
    }

    private async Task<ExpenseListResponse> BuildPageAsync(
        IQueryable<Expense> filtered, ListQuery query, CancellationToken ct)
    {
        var totalCount = await filtered.CountAsync(ct);

        var items = await MoneyResponseProjection.ExpenseList(
                ApplySort(filtered, query),
                _db.StoredFiles.AsNoTracking())
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new ExpenseListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private IQueryable<Expense> BuildListQuery(
        int portfolioId,
        int? propertyId,
        int? unitId,
        int? workOrderId,
        bool workOrderLinkedOnly,
        ListQuery query)
        => BuildListQuery(_db.Expenses.AsNoTracking(), portfolioId, propertyId, unitId,
            workOrderId, workOrderLinkedOnly, query);

    private IQueryable<Expense> BuildListQuery(
        IQueryable<Expense> q,
        int portfolioId,
        int? propertyId,
        int? unitId,
        int? workOrderId,
        bool workOrderLinkedOnly,
        ListQuery query)
    {
        q = q.Where(e => e.PortfolioId == portfolioId);

        if (propertyId.HasValue)
        {
            var pid = propertyId.Value;
            q = q.Where(e =>
                e.PropertyId == pid ||
                (e.WorkOrderId != null && _db.WorkOrders.Any(w => w.Id == e.WorkOrderId && w.PropertyId == pid)));
        }

        if (unitId.HasValue)
        {
            var uid = unitId.Value;
            if (workOrderLinkedOnly)
            {
                q = q.Where(e =>
                    e.WorkOrderId != null &&
                    _db.WorkOrders.Any(w => w.Id == e.WorkOrderId && w.UnitId == uid));
            }
            else
            {
                // The unit's expenses: directly tied to the unit OR tied to one of the unit's work orders.
                // The work-order set is a correlated subquery so this stays a single SQL statement (no N+1).
                q = q.Where(e =>
                    e.UnitId == uid ||
                    (e.WorkOrderId != null && _db.WorkOrders.Any(w => w.Id == e.WorkOrderId && w.UnitId == uid)));
            }
        }
        else if (workOrderLinkedOnly)
        {
            q = q.Where(e => e.WorkOrderId != null);
        }

        if (workOrderId.HasValue)
        {
            q = q.Where(e => e.WorkOrderId == workOrderId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(e =>
                EF.Functions.ILike(e.Description, $"%{term}%") ||
                (e.Notes != null && EF.Functions.ILike(e.Notes, $"%{term}%")));
        }

        if (query is ExpenseListQuery expenseQuery)
        {
            if (expenseQuery.OperationalScope.HasValue)
            {
                var operationalScope = expenseQuery.OperationalScope.Value;
                q = q.Where(e => e.OperationalScope == operationalScope);
            }

            if (expenseQuery.AllocationTargetKind.HasValue)
            {
                var targetKind = expenseQuery.AllocationTargetKind.Value;
                var targetId = expenseQuery.AllocationTargetId;
                q = q.Where(e => e.Allocations.Any(allocation =>
                    allocation.TargetKind == targetKind &&
                    (targetId == null ||
                     (targetKind == ExpenseAllocationTargetKind.Property &&
                      allocation.PropertyId == targetId) ||
                     (targetKind == ExpenseAllocationTargetKind.Unit &&
                      allocation.UnitId == targetId) ||
                     (targetKind == ExpenseAllocationTargetKind.OwnerEntity &&
                      allocation.OwnerEntityId == targetId))));
            }
            else if (expenseQuery.AllocationTargetId.HasValue)
            {
                var targetId = expenseQuery.AllocationTargetId.Value;
                q = q.Where(e => e.Allocations.Any(allocation =>
                    allocation.PropertyId == targetId ||
                    allocation.UnitId == targetId ||
                    allocation.OwnerEntityId == targetId));
            }

            if (expenseQuery.IncurredFrom.HasValue)
            {
                var incurredFrom = expenseQuery.IncurredFrom.Value.ToUtc();
                q = q.Where(e => e.IncurredAt >= incurredFrom);
            }

            if (expenseQuery.IncurredTo.HasValue)
            {
                var incurredToExclusive = ToExclusiveUpperBound(expenseQuery.IncurredTo.Value);
                q = q.Where(e => e.IncurredAt < incurredToExclusive);
            }

            if (expenseQuery.DueFrom.HasValue)
            {
                var dueFrom = expenseQuery.DueFrom.Value.ToUtc();
                q = q.Where(e => e.DueDate != null && e.DueDate >= dueFrom);
            }

            if (expenseQuery.DueTo.HasValue)
            {
                var dueToExclusive = ToExclusiveUpperBound(expenseQuery.DueTo.Value);
                q = q.Where(e => e.DueDate != null && e.DueDate < dueToExclusive);
            }

            if (expenseQuery.PaidFrom.HasValue)
            {
                var paidFrom = expenseQuery.PaidFrom.Value.ToUtc();
                q = q.Where(e => e.PaidAt != null && e.PaidAt >= paidFrom);
            }

            if (expenseQuery.PaidTo.HasValue)
            {
                var paidToExclusive = ToExclusiveUpperBound(expenseQuery.PaidTo.Value);
                q = q.Where(e => e.PaidAt != null && e.PaidAt < paidToExclusive);
            }
        }

        return q;
    }

    private static DateTime ToExclusiveUpperBound(DateTime value)
    {
        var utc = value.ToUtc();
        return value.TimeOfDay == TimeSpan.Zero ? utc.AddDays(1) : utc;
    }

    private static IQueryable<Expense> ApplySort(IQueryable<Expense> q, ListQuery query)
    {
        IOrderedQueryable<Expense> ordered = query.SortField switch
        {
            "description" => query.SortDescending ? q.OrderByDescending(e => e.Description) : q.OrderBy(e => e.Description),
            "category" => query.SortDescending ? q.OrderByDescending(e => e.Category) : q.OrderBy(e => e.Category),
            "status" => query.SortDescending ? q.OrderByDescending(e => e.Status) : q.OrderBy(e => e.Status),
            "amount" => query.SortDescending ? q.OrderByDescending(e => e.Amount) : q.OrderBy(e => e.Amount),
            "allocationtotal" => query.SortDescending
                ? q.OrderByDescending(e => e.Allocations.Select(a => (decimal?)a.Amount).Sum() ?? 0m)
                : q.OrderBy(e => e.Allocations.Select(a => (decimal?)a.Amount).Sum() ?? 0m),
            "operationalscope" => query.SortDescending
                ? q.OrderByDescending(e => e.OperationalScope)
                : q.OrderBy(e => e.OperationalScope),
            "incurredat" => query.SortDescending ? q.OrderByDescending(e => e.IncurredAt) : q.OrderBy(e => e.IncurredAt),
            "duedate" => query.SortDescending ? q.OrderByDescending(e => e.DueDate) : q.OrderBy(e => e.DueDate),
            "paidat" or "paiddate" => query.SortDescending ? q.OrderByDescending(e => e.PaidAt) : q.OrderBy(e => e.PaidAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(e => e.UpdatedAt) : q.OrderBy(e => e.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(e => e.CreatedAt) : q.OrderBy(e => e.CreatedAt),
        };
        return ordered.ThenBy(e => e.Id);
    }

    public Task<ExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default) =>
        GetAsync(_db.Expenses.AsNoTracking(), portfolioId, id, ct);

    public Task<ExpenseResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default) =>
        GetAsync(
            _db.Expenses.AsNoTracking().WhereMoneyAuthorized(
                _db, scope, CapabilityKeys.MoneyBalancesRead, _timeProvider.UtcNow()),
            scope.PortfolioId, id, ct);

    private async Task<ExpenseResponse?> GetAsync(
        IQueryable<Expense> expenses, int portfolioId, int id, CancellationToken ct)
    {
        var response = await MoneyResponseProjection.ExpenseDetails(
                expenses.Where(expense => expense.Id == id && expense.PortfolioId == portfolioId),
                _db.StoredFiles.AsNoTracking())
            .FirstOrDefaultAsync(ct);

        if (response == null)
            return null;

        var storedFile = await _db.FindLatestAvailableEntityFileAsync(
            _files, portfolioId, nameof(Expense), response.Id, ct);

        if (storedFile != null)
        {
            response.HasReceipt = true;
            response.ReceiptIsImage = storedFile.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        return response;
    }

    public async Task<ExpenseResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateExpenseRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Create, 0, idempotencyKey, request,
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        if (!outcome.Value.Found) return null;
        return ReadSnapshot<ExpenseResponse>(outcome.Value);
    }

    public async Task<ExpenseResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateExpenseRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Update, id, idempotencyKey, request,
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        if (!outcome.Value.Found) return null;
        return ReadSnapshot<ExpenseResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Delete, id, idempotencyKey, new object(),
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
