using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IExpenseService"/>
public class ExpenseService : IExpenseService
{
    private const string EntityType = "Expense";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public ExpenseService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<ExpenseResponse>> ListAsync(int portfolioId, int? propertyId, int? unitId, int? workOrderId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId);

        if (propertyId.HasValue)
        {
            q = q.Where(e => e.PropertyId == propertyId.Value);
        }

        if (unitId.HasValue)
        {
            // The unit's expenses: directly tied to the unit OR tied to one of the unit's work orders.
            // The work-order set is a correlated subquery so this stays a single SQL statement (no N+1).
            var uid = unitId.Value;
            q = q.Where(e =>
                e.UnitId == uid ||
                (e.WorkOrderId != null && _db.WorkOrders.Any(w => w.Id == e.WorkOrderId && w.UnitId == uid)));
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

        q = query.SortField switch
        {
            "description" => query.SortDescending ? q.OrderByDescending(e => e.Description) : q.OrderBy(e => e.Description),
            "category" => query.SortDescending ? q.OrderByDescending(e => e.Category) : q.OrderBy(e => e.Category),
            "status" => query.SortDescending ? q.OrderByDescending(e => e.Status) : q.OrderBy(e => e.Status),
            "amount" => query.SortDescending ? q.OrderByDescending(e => e.Amount) : q.OrderBy(e => e.Amount),
            "incurredat" => query.SortDescending ? q.OrderByDescending(e => e.IncurredAt) : q.OrderBy(e => e.IncurredAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(e => e.UpdatedAt) : q.OrderBy(e => e.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(e => e.CreatedAt) : q.OrderBy(e => e.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        // One batched query for all expense ids in this page — avoids N+1.
        var expenseIds = items.Select(e => e.Id).ToList();
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

    public async Task<ExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Expenses
            .AsNoTracking()
            .Include(e => e.LineItems)
            .Include(e => e.Property)
            .Include(e => e.Unit)
            .Include(e => e.Vendor)
            .FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);

        if (entity == null)
            return null;

        var response = ExpenseResponse.FromEntity(entity);
        response.LineItems = entity.LineItems
            .OrderBy(li => li.LineNumber)
            .Select(ExpenseLineItemResponse.FromEntity)
            .ToList();

        var storedFile = await _db.StoredFiles
            .AsNoTracking()
            .Where(f =>
                f.PortfolioId == portfolioId &&
                f.EntityType == "Expense" &&
                f.EntityId == entity.Id &&
                f.DeletedAt == null)
            .OrderByDescending(f => f.UploadedAt)
            .Select(f => new { f.ContentType })
            .FirstOrDefaultAsync(ct);

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

        var now = DateTime.UtcNow;
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

    public async Task<ExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateExpenseRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Expenses
            .Include(e => e.LineItems)
            .FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
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
        if (request.ReceiptData != null) entity.ReceiptData = request.ReceiptData;

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

        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = ExpenseResponse.FromEntity(entity);
        response.LineItems = entity.LineItems
            .OrderBy(li => li.LineNumber)
            .Select(ExpenseLineItemResponse.FromEntity)
            .ToList();
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
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

        entity.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }
}
