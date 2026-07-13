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

/// <inheritdoc cref="IExpenseService"/>
public class ExpenseService : IExpenseService
{
    private const string EntityType = "Expense";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IFileStorage _files;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork? _atomic;

    public ExpenseService(RentalCommandDbContext db, IDataUpdateService dataUpdate, IFileStorage files,
        TimeProvider timeProvider, IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _files = files;
        _timeProvider = timeProvider;
        _atomic = atomic;
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
        return await BuildPageAsync(filtered, portfolioId, query, ct);
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
        return BuildPageAsync(filtered, scope.PortfolioId, query, ct);
    }

    private async Task<ExpenseListResponse> BuildPageAsync(
        IQueryable<Expense> filtered, int portfolioId, ListQuery query, CancellationToken ct)
    {
        var totalCount = await filtered.CountAsync(ct);

        var items = await ApplySort(filtered, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        var results = await MapExpenseResponsesAsync(portfolioId, items, ct);

        return new ExpenseListResponse
        {
            Items = results,
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

    private static IQueryable<Expense> ApplySort(IQueryable<Expense> q, ListQuery query) =>
        query.SortField switch
        {
            "description" => query.SortDescending ? q.OrderByDescending(e => e.Description) : q.OrderBy(e => e.Description),
            "category" => query.SortDescending ? q.OrderByDescending(e => e.Category) : q.OrderBy(e => e.Category),
            "status" => query.SortDescending ? q.OrderByDescending(e => e.Status) : q.OrderBy(e => e.Status),
            "amount" => query.SortDescending ? q.OrderByDescending(e => e.Amount) : q.OrderBy(e => e.Amount),
            "incurredat" => query.SortDescending ? q.OrderByDescending(e => e.IncurredAt) : q.OrderBy(e => e.IncurredAt),
            "duedate" => query.SortDescending ? q.OrderByDescending(e => e.DueDate) : q.OrderBy(e => e.DueDate),
            "paidat" or "paiddate" => query.SortDescending ? q.OrderByDescending(e => e.PaidAt) : q.OrderBy(e => e.PaidAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(e => e.UpdatedAt) : q.OrderBy(e => e.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(e => e.CreatedAt) : q.OrderBy(e => e.CreatedAt),
        };

    private async Task<List<ExpenseResponse>> MapExpenseResponsesAsync(int portfolioId, IReadOnlyCollection<Expense> items, CancellationToken ct)
    {
        // One batched query for all expense ids in this page — avoids N+1.
        var expenseIds = items.Select(e => (long)e.Id).ToList();
        var filesByExpenseId = await _db.StoredFiles
            .AsNoTracking()
            .Where(f =>
                f.PortfolioId == portfolioId &&
                f.EntityType == "Expense" &&
                f.EntityId != null &&
                expenseIds.Contains(f.EntityId.Value) &&
                f.DeletedAt == null)
            .GroupBy(f => f.EntityId!.Value)
            .Select(g => new { EntityId = g.Key, ContentType = g.OrderByDescending(f => f.UploadedAt).First().ContentType })
            .ToDictionaryAsync(x => x.EntityId, x => x.ContentType, ct);

        var results = items.Select(e =>
        {
            var response = ExpenseResponse.FromEntity(e);
            if (filesByExpenseId.TryGetValue(e.Id, out var ct2))
            {
                response.HasReceipt = true;
                response.ReceiptIsImage = ct2.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            }
            return response;
        }).ToList();

        return results;
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
        var response = await expenses
            .Where(e => e.Id == id && e.PortfolioId == portfolioId)
            .Select(e => new ExpenseResponse
            {
                Id = e.Id,
                PortfolioId = e.PortfolioId,
                PropertyId = e.PropertyId,
                UnitId = e.UnitId,
                VendorId = e.VendorId,
                WorkOrderId = e.WorkOrderId,
                CapitalizedAssetId = e.CapitalizedAssetId,
                Category = e.Category,
                Description = e.Description,
                Status = e.Status,
                Amount = e.Amount,
                IncurredAt = e.IncurredAt,
                DueDate = e.DueDate,
                PaidAt = e.PaidAt,
                BillableToOwner = e.BillableToOwner,
                Notes = e.Notes,
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt,
                Subtotal = e.Subtotal,
                TaxAmount = e.TaxAmount,
                ReceiptData = e.ReceiptData,
                PaymentMethod = e.PaymentMethod,
                CardLast4 = e.CardLast4,
                DocumentKind = e.DocumentKind,
                PropertyName = e.Property == null ? null : e.Property.Name,
                UnitNumber = e.Unit == null ? null : e.Unit.UnitNumber,
                VendorName = e.Vendor == null ? null : e.Vendor.Name,
                LineItems = e.LineItems
                    .OrderBy(li => li.LineNumber)
                    .ThenBy(li => li.Id)
                    .Select(li => new ExpenseLineItemResponse
                    {
                        Description = li.Description,
                        Quantity = li.Quantity,
                        UnitPrice = li.UnitPrice,
                        Amount = li.Amount,
                        LineNumber = li.LineNumber,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        if (response == null)
            return null;

        var storedFile = await _db.FindLatestAvailableEntityFileAsync(_files, portfolioId, EntityType, response.Id, ct);

        if (storedFile != null)
        {
            response.HasReceipt = true;
            response.ReceiptIsImage = storedFile.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        return response;
    }

    public async Task<ExpenseResponse?> CreateAsync(int portfolioId, CreateExpenseRequest request, CancellationToken ct = default)
    {
        // Verify any supplied property/unit/vendor/work-order references belong to the caller's portfolio (no cross-tenant linking).
        if (request.PropertyId.HasValue &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId.Value, ct))
        {
            return null;
        }

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, request.PropertyId, ct))
        {
            return null;
        }

        if (request.VendorId.HasValue &&
            !await _db.EnsureVendorInPortfolioAsync(portfolioId, request.VendorId.Value, ct))
        {
            return null;
        }

        if (request.WorkOrderId.HasValue &&
            !await _db.EnsureWorkOrderInPortfolioAsync(portfolioId, request.WorkOrderId.Value, ct))
        {
            return null;
        }

        var now = _timeProvider.UtcNow();
        var entity = new Expense
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            VendorId = request.VendorId,
            WorkOrderId = request.WorkOrderId,
            Category = request.Category,
            Description = request.Description,
            Status = request.Status,
            Amount = request.Amount,
            IncurredAt = request.IncurredAt.ToUtc(),
            DueDate = request.DueDate.ToUtc(),
            PaidAt = request.PaidAt.ToUtc(),
            BillableToOwner = request.BillableToOwner,
            Notes = request.Notes,
            Subtotal = request.Subtotal,
            TaxAmount = request.TaxAmount,
            ReceiptData = request.ReceiptData,
            PaymentMethod = request.PaymentMethod,
            CardLast4 = request.CardLast4,
            DocumentKind = request.DocumentKind,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Promote scanned receipt line items to queryable child rows (cascade-deleted with the expense).
        foreach (var li in request.LineItems)
        {
            entity.LineItems.Add(new ExpenseLineItem
            {
                Description = li.Description ?? string.Empty,
                Quantity = li.Quantity,
                UnitPrice = li.UnitPrice,
                Amount = li.Amount,
                LineNumber = li.LineNumber,
            });
        }

        _db.Expenses.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = ExpenseResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<ExpenseResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateExpenseRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Create, 0, idempotencyKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        if (!outcome.Value.Found) return null;
        var response = await GetAsync(scope.PortfolioId, outcome.Value.EntityId, ct);
        if (response is not null)
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    public async Task<ExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateExpenseRequest request, CancellationToken ct = default)
    {
        IQueryable<Expense> expenseQuery = _db.Expenses;
        if (request.LineItems is not null)
        {
            expenseQuery = expenseQuery.Include(e => e.LineItems);
        }

        var entity = await expenseQuery.FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        // Verify any supplied property/unit/vendor/work-order references belong to the caller's portfolio (no cross-tenant linking).
        if (request.PropertyId.HasValue &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId.Value, ct))
        {
            return null;
        }

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, request.PropertyId, ct))
        {
            return null;
        }

        if (request.VendorId.HasValue &&
            !await _db.EnsureVendorInPortfolioAsync(portfolioId, request.VendorId.Value, ct))
        {
            return null;
        }

        if (request.WorkOrderId.HasValue &&
            !await _db.EnsureWorkOrderInPortfolioAsync(portfolioId, request.WorkOrderId.Value, ct))
        {
            return null;
        }

        if (request.PropertyId.HasValue) entity.PropertyId = request.PropertyId;
        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.VendorId.HasValue) entity.VendorId = request.VendorId;
        if (request.WorkOrderId.HasValue) entity.WorkOrderId = request.WorkOrderId;
        if (request.Category.HasValue) entity.Category = request.Category.Value;
        if (request.Description != null) entity.Description = request.Description;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.Amount.HasValue) entity.Amount = request.Amount.Value;
        if (request.IncurredAt.HasValue) entity.IncurredAt = request.IncurredAt.Value.ToUtc();
        if (request.DueDate.HasValue) entity.DueDate = request.DueDate.ToUtc();
        if (request.PaidAt.HasValue) entity.PaidAt = request.PaidAt.ToUtc();
        if (request.BillableToOwner.HasValue) entity.BillableToOwner = request.BillableToOwner.Value;
        if (request.Notes != null) entity.Notes = request.Notes;
        if (request.Subtotal.HasValue) entity.Subtotal = request.Subtotal;
        if (request.TaxAmount.HasValue) entity.TaxAmount = request.TaxAmount;
        if (request.ClearReceiptData == true) entity.ReceiptData = null;
        else if (request.ReceiptData != null) entity.ReceiptData = request.ReceiptData;

        // When the caller provides a LineItems list (even empty), REPLACE all existing rows.
        // A null LineItems means "leave existing rows untouched".
        if (request.LineItems is not null)
        {
            _db.ExpenseLineItems.RemoveRange(entity.LineItems);
            entity.LineItems.Clear();

            for (int i = 0; i < request.LineItems.Count; i++)
            {
                var li = request.LineItems[i];
                entity.LineItems.Add(new ExpenseLineItem
                {
                    Description = li.Description ?? string.Empty,
                    Quantity = li.Quantity,
                    UnitPrice = li.UnitPrice,
                    Amount = li.Amount,
                    LineNumber = i + 1,
                });
            }
        }

        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, id, ct) ?? ExpenseResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<ExpenseResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateExpenseRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        if (await GetAsync(scope, id, ct) is null)
            return null;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Update, id, idempotencyKey, request);
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
        var entity = await _db.Expenses
            .FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

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
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Delete, id, idempotencyKey, new object());
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
        "Scoped expense mutations require the atomic persistence kernel.");

    private async Task<bool> HasExpenseTargetCapabilityAsync(
        WorkspaceReadScope scope, int? propertyId, int? unitId, int? workOrderId,
        string capabilityKey, CancellationToken ct)
    {
        var now = _timeProvider.UtcNow();
        if (propertyId is null && unitId is null && workOrderId is null)
        {
            return await _db.AuthorizedAllPropertyAssignments(
                    scope, capabilityKey, CapabilityAuthorizationTargetKind.Property, now)
                .AnyAsync(ct);
        }

        var properties = _db.Properties.AsNoTracking().WhereAuthorized(_db, scope, capabilityKey, now);
        return await properties.AnyAsync(property =>
            (propertyId == null || property.Id == propertyId) &&
            (unitId == null || _db.Units.Any(unit =>
                unit.Id == unitId && unit.PortfolioId == scope.PortfolioId && unit.PropertyId == property.Id)) &&
            (workOrderId == null || _db.WorkOrders.Any(workOrder =>
                workOrder.Id == workOrderId && workOrder.PortfolioId == scope.PortfolioId &&
                workOrder.PropertyId == property.Id)), ct);
    }
}
