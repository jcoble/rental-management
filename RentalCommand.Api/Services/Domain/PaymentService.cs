using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
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
    private readonly IFileStorage _files;

    public PaymentService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit,
        IFileStorage files)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _audit = audit;
        _files = files;
    }

    // Money movement is high-stakes: status changes (reversals / refunds / waivers), collection, and
    // deletion get an explicit audit row with a full before→after snapshot + human reason. The explicit
    // log enriches the generic twin in place (see AuditTrailService), so writing it after SaveChanges wins.
    private static string Snapshot(Payment p) => JsonSerializer.Serialize(new
    {
        paymentType = p.PaymentType.ToString(),
        status = p.Status.ToString(),
        amount = p.Amount,
        amountPaid = p.AmountPaid,
        dueDate = p.DueDate,
        paidDate = p.PaidDate,
        method = p.Method,
        externalReference = p.ExternalReference,
        notes = p.Notes,
        leaseId = p.LeaseId,
    });

    /// <summary>
    /// Normalizes <see cref="Payment.AmountPaid"/> for the payment's final <paramref name="status"/> +
    /// <paramref name="amount"/>, and validates the partial-payment invariant, throwing
    /// <see cref="DomainValidationException"/> (400) on a bad request:
    /// <list type="bullet">
    ///   <item><b>Partial</b> — requires a supplied AmountPaid strictly between 0 and Amount (the
    ///   unpaid remainder <c>Amount − AmountPaid</c> is what stays owed).</item>
    ///   <item><b>Paid</b> — fully collected; AmountPaid is left null and aggregations treat the whole
    ///   Amount as collected. A supplied value above Amount is still rejected.</item>
    ///   <item><b>Anything else</b> (Scheduled/Late/Waived/Failed/Refunded) — AmountPaid is cleared to
    ///   null; nothing is collected yet.</item>
    /// </list>
    /// </summary>
    private static decimal? NormalizeAmountPaid(PaymentStatus status, decimal amount, decimal? amountPaid)
    {
        // A collected amount can never exceed the charge, whatever the status.
        if (amountPaid.HasValue && amountPaid.Value > amount)
        {
            throw new DomainValidationException(
                "Amount paid cannot be greater than the payment amount.");
        }

        if (status == PaymentStatus.Partial)
        {
            if (!amountPaid.HasValue || amountPaid.Value <= 0m || amountPaid.Value >= amount)
            {
                throw new DomainValidationException(
                    "A partial payment requires an amount paid greater than 0 and less than the full amount.");
            }

            return amountPaid.Value;
        }

        // Paid is treated as fully collected (whole Amount) with AmountPaid left null; every other
        // status has nothing collected. Either way the partial split column is cleared.
        return null;
    }

    public async Task<IReadOnlyList<PaymentResponse>> ListAsync(int portfolioId, int? leaseId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, leaseId, query, ct);
        return page.Items;
    }

    public async Task<PaymentListResponse> ListPageAsync(int portfolioId, int? leaseId, ListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(portfolioId, leaseId, query);
        var totalCount = await filtered.CountAsync(ct);

        var items = await ApplySort(filtered, query)
            .Include(p => p.Lease!).ThenInclude(l => l.Tenant)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new PaymentListResponse
        {
            Items = items.Select(PaymentResponse.FromEntity).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private IQueryable<Payment> BuildListQuery(int portfolioId, int? leaseId, ListQuery query)
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

        return q;
    }

    private static IQueryable<Payment> ApplySort(IQueryable<Payment> q, ListQuery query) =>
        query.SortField switch
        {
            "amount" => query.SortDescending ? q.OrderByDescending(p => p.Amount) : q.OrderBy(p => p.Amount),
            "duedate" => query.SortDescending ? q.OrderByDescending(p => p.DueDate) : q.OrderBy(p => p.DueDate),
            "paiddate" => query.SortDescending ? q.OrderByDescending(p => p.PaidDate) : q.OrderBy(p => p.PaidDate),
            "status" => query.SortDescending ? q.OrderByDescending(p => p.Status) : q.OrderBy(p => p.Status),
            "updatedat" => query.SortDescending ? q.OrderByDescending(p => p.UpdatedAt) : q.OrderBy(p => p.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt),
        };

    public async Task<PaymentResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Payments
            .AsNoTracking()
            .Include(p => p.Lease!).ThenInclude(l => l.Tenant)
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);

        if (entity == null)
        {
            return null;
        }

        var response = PaymentResponse.FromEntity(entity);
        var scan = await _db.FindLatestAvailableEntityFileAsync(_files, portfolioId, EntityType, id, ct);
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
            // Partial-aware split: validated + normalized for the final status/amount (Partial keeps the
            // collected-so-far; Paid/everything-else clears to null).
            AmountPaid = NormalizeAmountPaid(request.Status, request.Amount, request.AmountPaid),
            DueDate = request.DueDate.ToUtc(),
            // A payment created as Paid must carry a PaidDate or it's invisible to the income/collected
            // aggregations (which require PaidDate != null). Prefer DueDate so income lands in the right period.
            PaidDate = request.Status == PaymentStatus.Paid
                ? (request.PaidDate.ToUtc() ?? request.DueDate.ToUtc())
                : request.PaidDate.ToUtc(),
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
        var prevNotes = entity.Notes;

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

        // Partial-aware split, validated against the payment's resulting status + amount. A supplied
        // AmountPaid wins; otherwise the existing value is re-normalized (so changing the status away from
        // Partial clears a stale collected-so-far, and changing it TO Partial requires a value).
        entity.AmountPaid = NormalizeAmountPaid(
            entity.Status, entity.Amount,
            request.AmountPaid ?? entity.AmountPaid);

        // A payment that ends up Paid without a PaidDate is invisible to income/collected reports.
        // Default it (preferring DueDate so income lands in the right period) — covers transitions TO Paid.
        if (entity.Status == PaymentStatus.Paid && entity.PaidDate is null)
        {
            entity.PaidDate = request.PaidDate.ToUtc() ?? entity.DueDate;
        }

        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // A status, amount, lease, or note change is detail-history material: notes often carry the
        // human explanation for a money event, and losing them makes the audit diff misleading.
        if (entity.Status != prevStatus || entity.Amount != prevAmount || entity.LeaseId != prevLeaseId || entity.Notes != prevNotes)
        {
            var changes = new List<string>();
            if (entity.Status != prevStatus) changes.Add($"status {prevStatus}→{entity.Status}");
            if (entity.Amount != prevAmount) changes.Add($"amount {prevAmount:0.##}→{entity.Amount:0.##}");
            if (entity.LeaseId != prevLeaseId) changes.Add($"lease {prevLeaseId}→{entity.LeaseId}");
            if (entity.Notes != prevNotes) changes.Add("notes updated");
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
        // Marking paid means fully collected: clear any partial split so the whole Amount counts as collected.
        entity.AmountPaid = null;
        entity.PaidDate = request.PaidDate?.ToUtc() ?? DateTime.UtcNow;
        if (request.Method != null) entity.Method = request.Method;
        if (request.ExternalReference != null) entity.ExternalReference = request.ExternalReference;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(portfolioId, EntityType, entity.Id, AuditLogOperation.Updated,
            oldValues: before, newValues: Snapshot(entity),
            changeReason: $"Payment #{entity.Id} marked paid (${entity.Amount:0.##})", ct: ct);

        var response = PaymentResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<MarkLeasePastDuePaidResponse?> MarkLeasePastDuePaidAsync(
        int portfolioId,
        int leaseId,
        MarkPaidRequest request,
        CancellationToken ct = default)
    {
        var leaseExists = await _db.Leases
            .AsNoTracking()
            .AnyAsync(l => l.Id == leaseId && l.PortfolioId == portfolioId, ct);
        if (!leaseExists)
            return null;

        var now = DateTime.UtcNow;
        var paidDate = request.PaidDate?.ToUtc() ?? now;

        var entities = await _db.Payments
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.LeaseId == leaseId &&
                (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial || p.Status == PaymentStatus.Late) &&
                (p.Status == PaymentStatus.Late || p.DueDate < now))
            .OrderBy(p => p.DueDate)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);

        if (entities.Count == 0)
        {
            return new MarkLeasePastDuePaidResponse
            {
                LeaseId = leaseId,
                MarkedPaidCount = 0,
                PaymentIds = [],
            };
        }

        var before = entities.ToDictionary(p => p.Id, Snapshot);

        foreach (var entity in entities)
        {
            entity.Status = PaymentStatus.Paid;
            entity.AmountPaid = null;
            entity.PaidDate = paidDate;
            if (request.Method != null) entity.Method = request.Method;
            if (request.ExternalReference != null) entity.ExternalReference = request.ExternalReference;
            if (request.Notes != null) entity.Notes = request.Notes;
            entity.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        foreach (var entity in entities)
        {
            await _audit.LogAsync(portfolioId, EntityType, entity.Id, AuditLogOperation.Updated,
                oldValues: before[entity.Id], newValues: Snapshot(entity),
                changeReason: $"Payment #{entity.Id} marked paid from past-due lease action (${entity.Amount:0.##})", ct: ct);

            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, PaymentResponse.FromEntity(entity), ct);
        }

        return new MarkLeasePastDuePaidResponse
        {
            LeaseId = leaseId,
            MarkedPaidCount = entities.Count,
            PaymentIds = entities.Select(p => p.Id).ToList(),
        };
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
