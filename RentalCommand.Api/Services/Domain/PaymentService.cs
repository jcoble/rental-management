using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
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
    private readonly TimeProvider _timeProvider;

    public PaymentService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit,
        IFileStorage files,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _audit = audit;
        _files = files;
        _timeProvider = timeProvider;
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

    private async Task EnsureGeneratedPeriodPaymentKeyAvailableAsync(
        int portfolioId,
        int paymentId,
        int targetLeaseId,
        PaymentType targetPaymentType,
        string? periodKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(periodKey))
        {
            return;
        }

        var alreadyExists = await _db.Payments
            .AsNoTracking()
            .AnyAsync(p =>
                p.PortfolioId == portfolioId &&
                p.Id != paymentId &&
                p.LeaseId == targetLeaseId &&
                p.PaymentType == targetPaymentType &&
                p.PeriodKey == periodKey, ct);

        if (!alreadyExists)
        {
            return;
        }

        throw new DomainValidationException(
            $"The selected lease already has a generated {FormatPaymentType(targetPaymentType)} payment for {FormatPeriodKey(periodKey)}. Open the existing payment instead, or choose a different lease.",
            statusCode: StatusCodes.Status409Conflict);
    }

    private static string FormatPaymentType(PaymentType paymentType) =>
        paymentType switch
        {
            PaymentType.Rent => "rent",
            PaymentType.SecurityDeposit => "security deposit",
            PaymentType.LateFee => "late fee",
            PaymentType.Utility => "utility",
            _ => "ledger",
        };

    private static string FormatPeriodKey(string periodKey)
    {
        if (DateTime.TryParseExact(
                periodKey,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var period))
        {
            return period.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        }

        return periodKey;
    }

    public async Task<IReadOnlyList<PaymentReceiptResponse>> ListAsync(int portfolioId, PaymentListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, query, ct);
        return page.Items;
    }

    public async Task<PaymentListResponse> ListPageAsync(int portfolioId, PaymentListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildReceiptQuery(portfolioId, query);
        var totalCount = await filtered.CountAsync(ct);

        var items = await ApplyReceiptSort(filtered, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new PaymentListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    internal IQueryable<PaymentReceiptResponse> BuildReceiptQuery(int portfolioId, PaymentListQuery query)
    {
        var q =
            from entry in _db.TenantLedgerEntries.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { entry.PortfolioId, Id = entry.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            where entry.PortfolioId == portfolioId
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
            select new PaymentReceiptResponse
            {
                Id = entry.Id,
                PublicId = entry.PublicId,
                PortfolioId = entry.PortfolioId,
                TenantAccountId = account.Id,
                LeaseManagementId = management.Id,
                PropertyId = management.PropertyId,
                UnitId = management.UnitId,
                AccountNumber = account.AccountNumber,
                RelationshipNumber = management.RelationshipNumber,
                TenantName = lifecycle.CurrentPrimaryTenantName,
                PropertyName = management.Property!.Name,
                UnitNumber = management.Unit!.UnitNumber,
                Amount = entry.Amount,
                Currency = entry.Currency,
                ReceivedOn = entry.EffectiveOn,
                PostedAtUtc = entry.PostedAtUtc,
                Description = entry.Description,
                Provider = entry.ProviderPaymentAttempt == null ? null : entry.ProviderPaymentAttempt.Provider,
                ProviderReference = entry.ProviderPaymentAttempt == null ? null : entry.ProviderPaymentAttempt.ProviderObjectId,
                ProviderState = entry.ProviderPaymentAttempt == null ? null : entry.ProviderPaymentAttempt.State,
                PaymentMethodSummary = entry.ProviderPaymentAttempt == null ? null : entry.ProviderPaymentAttempt.PaymentMethodSummary,
                PayerName = entry.ProviderPaymentAttempt == null ? null : entry.ProviderPaymentAttempt.PayerName,
                CheckNumber = entry.ProviderPaymentAttempt == null ? null : entry.ProviderPaymentAttempt.CheckNumber,
                BankName = entry.ProviderPaymentAttempt == null ? null : entry.ProviderPaymentAttempt.BankName,
                SourceStoredFileId = entry.SourceStoredFileId,
            };

        if (query.TenantAccountId is int tenantAccountId)
            q = q.Where(row => row.TenantAccountId == tenantAccountId);
        if (query.LeaseManagementId is int leaseManagementId)
            q = q.Where(row => row.LeaseManagementId == leaseManagementId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(row =>
                EF.Functions.ILike(row.Description, $"%{term}%") ||
                EF.Functions.ILike(row.AccountNumber, $"%{term}%") ||
                EF.Functions.ILike(row.RelationshipNumber, $"%{term}%") ||
                (row.ProviderReference != null && EF.Functions.ILike(row.ProviderReference, $"%{term}%")) ||
                (row.PayerName != null && EF.Functions.ILike(row.PayerName, $"%{term}%")) ||
                (row.CheckNumber != null && EF.Functions.ILike(row.CheckNumber, $"%{term}%")));
        }

        if (query.PaidFrom.HasValue)
        {
            var paidFrom = DateOnly.FromDateTime(query.PaidFrom.Value.ToUtc());
            q = q.Where(row => row.ReceivedOn >= paidFrom);
        }
        if (query.PaidTo.HasValue)
        {
            var paidToExclusive = DateOnly.FromDateTime(ToExclusiveUpperBound(query.PaidTo.Value));
            q = q.Where(row => row.ReceivedOn < paidToExclusive);
        }

        return q;
    }

    private static DateTime ToExclusiveUpperBound(DateTime value)
    {
        var utc = value.ToUtc();
        return value.TimeOfDay == TimeSpan.Zero ? utc.AddDays(1) : utc;
    }

    private static IQueryable<PaymentReceiptResponse> ApplyReceiptSort(
        IQueryable<PaymentReceiptResponse> q,
        ListQuery query) =>
        query.SortField switch
        {
            "amount" => query.SortDescending ? q.OrderByDescending(row => row.Amount) : q.OrderBy(row => row.Amount),
            "receivedon" or "paiddate" => query.SortDescending ? q.OrderByDescending(row => row.ReceivedOn) : q.OrderBy(row => row.ReceivedOn),
            "tenant" => query.SortDescending ? q.OrderByDescending(row => row.TenantName) : q.OrderBy(row => row.TenantName),
            _ => query.SortDescending
                ? q.OrderByDescending(row => row.PostedAtUtc).ThenByDescending(row => row.Id)
                : q.OrderBy(row => row.PostedAtUtc).ThenBy(row => row.Id),
        };

    public Task<PaymentReceiptResponse?> GetAsync(int portfolioId, long id, CancellationToken ct = default)
    {
        return BuildReceiptQuery(portfolioId, new PaymentListQuery())
            .SingleOrDefaultAsync(row => row.Id == id, ct);
    }

    public async Task<PaymentResponse?> CreateAsync(int portfolioId, CreatePaymentRequest request, CancellationToken ct = default)
    {
        // Exactly one of LeaseId / ApplicationId identifies what the payment is charged against: a lease
        // (rent/late-fee/utility) or a rental application (a lease-less application/screening fee). Both or
        // neither is a validation failure (400).
        if ((request.LeaseId is not null) == (request.ApplicationId is not null))
        {
            throw new DomainValidationException(
                "A payment must reference exactly one of a lease or an application.");
        }

        // Every referenced FK must belong to the caller's portfolio (cross-tenant IDOR guard) → 404 on miss.
        if (request.LeaseId is { } leaseId && !await _db.EnsureLeaseInPortfolioAsync(portfolioId, leaseId, ct))
        {
            return null;
        }
        if (request.ApplicationId is { } applicationId && !await _db.EnsureApplicationInPortfolioAsync(portfolioId, applicationId, ct))
        {
            return null;
        }
        if (request.PropertyId is { } requestedPropertyId && !await _db.EnsurePropertyInPortfolioAsync(portfolioId, requestedPropertyId, ct))
        {
            return null;
        }

        // An application fee defaults its type to ApplicationFee and attributes to the application's property
        // when the caller didn't pass one, so lease-less income lands on the right property's reports.
        var paymentType = request.PaymentType;
        var resolvedPropertyId = request.PropertyId;
        if (request.ApplicationId is { } appId)
        {
            paymentType = PaymentType.ApplicationFee;
            resolvedPropertyId ??= await _db.RentalApplications
                .Where(a => a.Id == appId && a.PortfolioId == portfolioId)
                .Select(a => a.PropertyId)
                .FirstOrDefaultAsync(ct);
        }

        var now = _timeProvider.UtcNow();
        var entity = new Payment
        {
            PortfolioId = portfolioId,
            LeaseId = request.LeaseId,
            ApplicationId = request.ApplicationId,
            PropertyId = resolvedPropertyId,
            PaymentType = paymentType,
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

        var targetLeaseId = request.LeaseId ?? entity.LeaseId;
        var targetPaymentType = request.PaymentType ?? entity.PaymentType;

        if (targetLeaseId != entity.LeaseId)
        {
            // Reassigning a payment to a different lease moves it onto that lease's ledger — the target
            // lease must belong to the caller's portfolio (cross-tenant IDOR guard, mirroring CreateAsync).
            // A reassignment always targets a real lease (request.LeaseId was supplied and differs).
            if (targetLeaseId is not { } newLeaseId ||
                !await _db.EnsureLeaseInPortfolioAsync(portfolioId, newLeaseId, ct))
            {
                return null;
            }
        }

        // The (lease, type, period) uniqueness only constrains lease-tied auto-generated rows
        // (PeriodKey != null ⇒ a lease); a lease-less payment (application fee) has no PeriodKey to guard.
        if (targetLeaseId is { } periodLeaseId &&
            (targetLeaseId != entity.LeaseId || targetPaymentType != entity.PaymentType))
        {
            await EnsureGeneratedPeriodPaymentKeyAvailableAsync(
                portfolioId,
                id,
                periodLeaseId,
                targetPaymentType,
                entity.PeriodKey,
                ct);
        }

        if (targetLeaseId != entity.LeaseId) entity.LeaseId = targetLeaseId;
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

        entity.UpdatedAt = _timeProvider.UtcNow();

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
        entity.PaidDate = request.PaidDate?.ToUtc() ?? _timeProvider.UtcNow();
        if (request.Method != null) entity.Method = request.Method;
        if (request.ExternalReference != null) entity.ExternalReference = request.ExternalReference;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = _timeProvider.UtcNow();

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

        var now = _timeProvider.UtcNow();
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
