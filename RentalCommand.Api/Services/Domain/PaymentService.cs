using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPaymentService"/>
public class PaymentService : IPaymentService
{
    private const string EntityType = "Payment";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;

    public PaymentService(RentalCommandDbContext db, IDataUpdateService dataUpdate, IAuditTrailService audit)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _audit = audit;
    }

    // Money movement is high-stakes: status changes (reversals / refunds / waivers), collection, and
    // deletion get an explicit audit row with a full before→after snapshot + human reason. The explicit
    // log enriches the generic twin in place (see AuditTrailService), so writing it after SaveChanges wins.
    private static string Snapshot(Payment p) => JsonSerializer.Serialize(new
    {
        paymentType = p.PaymentType.ToString(),
        status = p.Status.ToString(),
        amount = p.Amount,
        dueDate = p.DueDate,
        paidDate = p.PaidDate,
        method = p.Method,
        externalReference = p.ExternalReference,
        leaseId = p.LeaseId,
    });

    public async Task<IReadOnlyList<PaymentResponse>> ListAsync(int portfolioId, int? leaseId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId);

        if (leaseId.HasValue)
        {
            q = q.Where(p => p.LeaseId == leaseId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(p =>
                (p.Method != null && EF.Functions.ILike(p.Method, $"%{term}%")) ||
                (p.ExternalReference != null && EF.Functions.ILike(p.ExternalReference, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "amount" => query.SortDescending ? q.OrderByDescending(p => p.Amount) : q.OrderBy(p => p.Amount),
            "duedate" => query.SortDescending ? q.OrderByDescending(p => p.DueDate) : q.OrderBy(p => p.DueDate),
            "paiddate" => query.SortDescending ? q.OrderByDescending(p => p.PaidDate) : q.OrderBy(p => p.PaidDate),
            "status" => query.SortDescending ? q.OrderByDescending(p => p.Status) : q.OrderBy(p => p.Status),
            "updatedat" => query.SortDescending ? q.OrderByDescending(p => p.UpdatedAt) : q.OrderBy(p => p.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(PaymentResponse.FromEntity).ToList();
    }

    public async Task<PaymentResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);

        if (entity == null)
        {
            return null;
        }

        var response = PaymentResponse.FromEntity(entity);
        var scan = await _db.FindLatestEntityFileAsync(portfolioId, "Payment", id, ct);
        if (scan is not null)
        {
            response.HasScan = true;
            response.ScanIsImage = scan.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        return response;
    }

    public async Task<PaymentResponse?> CreateAsync(int portfolioId, CreatePaymentRequest request, CancellationToken ct = default)
    {
        // Verify the referenced lease belongs to the caller's portfolio.
        if (!await _db.EnsureLeaseInPortfolioAsync(portfolioId, request.LeaseId, ct))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new Payment
        {
            PortfolioId = portfolioId,
            LeaseId = request.LeaseId,
            PaymentType = request.PaymentType,
            Status = request.Status,
            Amount = request.Amount,
            DueDate = request.DueDate.ToUtc(),
            PaidDate = request.PaidDate.ToUtc(),
            Method = request.Method,
            ExternalReference = request.ExternalReference,
            Notes = request.Notes,
            PayerName = request.PayerName,
            CheckNumber = request.CheckNumber,
            BankName = request.BankName,
            ExtractedData = request.ExtractedData,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Payments.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = PaymentResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<PaymentResponse?> UpdateAsync(int portfolioId, int id, UpdatePaymentRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Payments
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        var before = Snapshot(entity);
        var prevStatus = entity.Status;
        var prevAmount = entity.Amount;
        var prevLeaseId = entity.LeaseId;

        if (request.LeaseId.HasValue && request.LeaseId.Value != entity.LeaseId)
        {
            // Reassigning a payment to a different lease moves it onto that lease's ledger — the target
            // lease must belong to the caller's portfolio (cross-tenant IDOR guard, mirroring CreateAsync).
            if (!await _db.EnsureLeaseInPortfolioAsync(portfolioId, request.LeaseId.Value, ct))
            {
                return null;
            }

            entity.LeaseId = request.LeaseId.Value;
        }

        if (request.PaymentType.HasValue) entity.PaymentType = request.PaymentType.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.Amount.HasValue) entity.Amount = request.Amount.Value;
        if (request.DueDate.HasValue) entity.DueDate = request.DueDate.Value.ToUtc();
        if (request.PaidDate.HasValue) entity.PaidDate = request.PaidDate.ToUtc();
        if (request.Method != null) entity.Method = request.Method;
        if (request.ExternalReference != null) entity.ExternalReference = request.ExternalReference;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // A status, amount, or lease change is the legally-meaningful event (reversal / refund / waiver /
        // re-statement / re-attribution) — record the original and new state in full.
        if (entity.Status != prevStatus || entity.Amount != prevAmount || entity.LeaseId != prevLeaseId)
        {
            var changes = new List<string>();
            if (entity.Status != prevStatus) changes.Add($"status {prevStatus}→{entity.Status}");
            if (entity.Amount != prevAmount) changes.Add($"amount {prevAmount:0.##}→{entity.Amount:0.##}");
            if (entity.LeaseId != prevLeaseId) changes.Add($"lease {prevLeaseId}→{entity.LeaseId}");
            await _audit.LogAsync(portfolioId, EntityType, entity.Id, AuditLogOperation.Updated,
                oldValues: before, newValues: Snapshot(entity),
                changeReason: $"Payment #{entity.Id}: {string.Join("; ", changes)}", ct: ct);
        }

        var response = PaymentResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<PaymentResponse?> MarkPaidAsync(int portfolioId, int id, MarkPaidRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Payments
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        var before = Snapshot(entity);

        entity.Status = PaymentStatus.Paid;
        entity.PaidDate = request.PaidDate?.ToUtc() ?? DateTime.UtcNow;
        if (request.Method != null) entity.Method = request.Method;
        if (request.ExternalReference != null) entity.ExternalReference = request.ExternalReference;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(portfolioId, EntityType, entity.Id, AuditLogOperation.Updated,
            oldValues: before, newValues: Snapshot(entity),
            changeReason: $"Payment #{entity.Id} marked paid (${entity.Amount:0.##})", ct: ct);

        var response = PaymentResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        // Payment has no soft-delete column, so this is a hard delete.
        var entity = await _db.Payments
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        var before = Snapshot(entity);

        _db.Payments.Remove(entity);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(portfolioId, EntityType, id, AuditLogOperation.Deleted,
            oldValues: before, changeReason: $"Payment #{id} deleted (${entity.Amount:0.##} {entity.PaymentType})", ct: ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }
}
