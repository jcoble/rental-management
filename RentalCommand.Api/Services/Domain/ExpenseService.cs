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

    public async Task<IReadOnlyList<ExpenseResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId);

        if (propertyId.HasValue)
        {
            q = q.Where(e => e.PropertyId == propertyId.Value);
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
            .FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);

        if (entity == null)
            return null;

        var response = ExpenseResponse.FromEntity(entity);

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
        // Verify any supplied property/vendor/work-order references belong to the caller's portfolio (no cross-tenant linking).
        if (request.PropertyId.HasValue &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId.Value, ct))
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
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Expenses.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = ExpenseResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<ExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateExpenseRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Expenses
            .FirstOrDefaultAsync(e => e.Id == id && e.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        // Verify any supplied property/vendor/work-order references belong to the caller's portfolio (no cross-tenant linking).
        if (request.PropertyId.HasValue &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId.Value, ct))
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
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = ExpenseResponse.FromEntity(entity);
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
