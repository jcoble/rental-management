using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ILeaseService"/>
public class LeaseService : ILeaseService
{
    private const string EntityType = "Lease";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IFileStorage _storage;
    private readonly ILeaseAgreementPdfGenerator _pdf;
    private readonly IAuditTrailService _audit;
    private readonly ILogger<LeaseService> _logger;

    public LeaseService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IFileStorage storage,
        ILeaseAgreementPdfGenerator pdf,
        IAuditTrailService audit,
        ILogger<LeaseService> logger)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _storage = storage;
        _pdf = pdf;
        _audit = audit;
        _logger = logger;
    }

    // A lease is a legal contract, so high-stakes events (create / edit / terminate) get an explicit
    // audit row with a GUARANTEED full before→after snapshot + human reason — richer than the generic
    // interceptor's changed-properties-only capture. The explicit log enriches the generic twin in
    // place (see AuditTrailService), so it wins regardless of being written after SaveChanges.
    private static string Snapshot(Lease l) => JsonSerializer.Serialize(new
    {
        leaseNumber = l.LeaseNumber,
        status = l.Status.ToString(),
        startDate = l.StartDate,
        endDate = l.EndDate,
        moveInDate = l.MoveInDate,
        moveOutDate = l.MoveOutDate,
        monthlyRent = l.MonthlyRent,
        securityDeposit = l.SecurityDeposit,
        lateFeeAmount = l.LateFeeAmount,
        rentDueDay = l.RentDueDay,
        tenantId = l.TenantId,
        propertyId = l.PropertyId,
        unitId = l.UnitId,
    });

    public async Task<IReadOnlyList<LeaseResponse>> ListAsync(int portfolioId, int? tenantId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Leases
            .AsNoTracking()
            .Include(l => l.Tenant)
            .Include(l => l.Unit)
            .Include(l => l.Property)
            .Where(l => l.PortfolioId == portfolioId);

        if (tenantId.HasValue)
        {
            q = q.Where(l => l.TenantId == tenantId.Value);
        }

        if (propertyId.HasValue)
        {
            q = q.Where(l => l.PropertyId == propertyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(l => EF.Functions.ILike(l.LeaseNumber, $"%{term}%"));
        }

        q = query.SortField switch
        {
            "leasenumber" => query.SortDescending ? q.OrderByDescending(l => l.LeaseNumber) : q.OrderBy(l => l.LeaseNumber),
            "status" => query.SortDescending ? q.OrderByDescending(l => l.Status) : q.OrderBy(l => l.Status),
            "startdate" => query.SortDescending ? q.OrderByDescending(l => l.StartDate) : q.OrderBy(l => l.StartDate),
            "enddate" => query.SortDescending ? q.OrderByDescending(l => l.EndDate) : q.OrderBy(l => l.EndDate),
            "monthlyrent" => query.SortDescending ? q.OrderByDescending(l => l.MonthlyRent) : q.OrderBy(l => l.MonthlyRent),
            "updatedat" => query.SortDescending ? q.OrderByDescending(l => l.UpdatedAt) : q.OrderBy(l => l.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(l => l.CreatedAt) : q.OrderBy(l => l.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(l => LeaseResponse.FromEntity(l, includeNavigations: true)).ToList();
    }

    public async Task<LeaseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Leases
            .AsNoTracking()
            .Include(l => l.Tenant)
            .Include(l => l.Unit)
            .Include(l => l.Property)
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);

        if (entity == null)
        {
            return null;
        }

        var response = LeaseResponse.FromEntity(entity, includeNavigations: true);
        var scan = await _db.FindLatestEntityFileAsync(portfolioId, "Lease", id, ct);
        if (scan is not null)
        {
            response.HasScan = true;
            response.ScanIsImage = scan.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        return response;
    }

    public async Task<LeaseLedgerResponse?> GetLedgerAsync(
        int portfolioId, int id, int? restrictToTenantId = null, CancellationToken ct = default)
    {
        var query = _db.Leases
            .AsNoTracking()
            .Include(l => l.Tenant)
            .Include(l => l.Property)
            .Where(l => l.Id == id && l.PortfolioId == portfolioId);

        // When a tenant calls this, they may only read THEIR OWN lease's ledger — the lookup itself
        // requires the tenant match, so a non-owned (or non-existent) lease returns null/404 without
        // revealing whether it exists. Landlord/staff callers pass null and see any lease in scope.
        if (restrictToTenantId is > 0)
        {
            query = query.Where(l => l.TenantId == restrictToTenantId.Value);
        }

        var lease = await query.FirstOrDefaultAsync(ct);

        if (lease == null)
        {
            return null;
        }

        var payments = await _db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == id && p.PortfolioId == portfolioId)
            .Select(p => new
            {
                p.Id,
                p.PaymentType,
                p.Status,
                p.Amount,
                p.DueDate,
                p.PaidDate,
                p.Method,
            })
            .ToListAsync(ct);

        // A lease may carry an opening balance migrated in from before Rental Command. It anchors the
        // ledger as the oldest entry so pre-app history isn't silently dropped.
        var opening = await _db.OpeningBalances
            .AsNoTracking()
            .Where(o => o.LeaseId == id && o.PortfolioId == portfolioId)
            .Select(o => new { o.Id, o.Amount, o.AsOfDate })
            .FirstOrDefaultAsync(ct);

        var tenantName = $"{lease.Tenant?.FirstName} {lease.Tenant?.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(tenantName)) tenantName = "Tenant";

        var entries = payments
            .Select(p =>
            {
                // A collected payment credits the tenant's balance (money in, shown positive). An
                // outstanding charge debits it (money owed, shown negative) so the running total reads
                // as "still owed".
                var isCollected = p.Status == PaymentStatus.Paid;
                var signedAmount = isCollected ? p.Amount : -p.Amount;

                return new LedgerTransactionResponse
                {
                    Date = p.PaidDate ?? p.DueDate,
                    Type = isCollected ? "Payment" : "Charge",
                    Id = p.Id,
                    Description = p.PaymentType.ToString(),
                    Amount = signedAmount,
                    PropertyId = lease.PropertyId,
                    PropertyName = lease.Property?.Name,
                    Counterparty = tenantName,
                    Category = p.PaymentType.ToString(),
                    Status = p.Status.ToString(),
                    SourceHref = $"/accounting/payments/{p.Id}",
                    Explanation = LedgerExplanation.ForPayment(
                        p.PaymentType, p.Status, p.Amount, p.DueDate, p.PaidDate, p.Method),
                };
            })
            .OrderByDescending(e => e.Date)
            .ThenByDescending(e => e.Id)
            .ToList();

        // Each payment row is a billed charge (rent, fee, deposit). "Charged" is every real charge;
        // "Paid" is what's been collected. Balance (charged − paid) is exactly what's still owed.
        // Waived/Failed/Refunded rows aren't money owed and weren't collected, so they're excluded
        // from both totals and net to zero in the balance.
        var totalCharged = payments
            .Where(p => p.Status is PaymentStatus.Scheduled or PaymentStatus.Partial
                or PaymentStatus.Late or PaymentStatus.Paid)
            .Sum(p => p.Amount);
        var totalPaid = payments
            .Where(p => p.Status == PaymentStatus.Paid)
            .Sum(p => p.Amount);

        if (opening != null)
        {
            // Anchor the ledger with the carried-over balance as its oldest entry. A positive amount
            // (tenant owed) reads as a charge; a negative amount (credit) reads like a prepayment. Folded
            // into the totals so the running balance reflects pre-app history: a positive opening adds to
            // "charged", a credit adds to "paid", keeping Balance = TotalCharged − TotalPaid exact.
            totalCharged += Math.Max(opening.Amount, 0m);
            totalPaid += Math.Max(-opening.Amount, 0m);

            entries.Add(new LedgerTransactionResponse
            {
                Date = opening.AsOfDate,
                Type = "Opening",
                Id = opening.Id,
                Description = "Opening balance",
                Amount = opening.Amount,
                PropertyId = lease.PropertyId,
                PropertyName = lease.Property?.Name,
                Counterparty = tenantName,
                Category = "Opening",
                Status = "Opening",
                SourceHref = $"/accounting/opening-balances/{opening.Id}",
                Explanation = LedgerExplanation.ForOpeningBalance(opening.Amount, opening.AsOfDate),
            });
        }

        return new LeaseLedgerResponse
        {
            LeaseId = lease.Id,
            LeaseNumber = lease.LeaseNumber,
            TenantName = tenantName,
            PropertyName = lease.Property?.Name,
            TotalCharged = totalCharged,
            TotalPaid = totalPaid,
            Balance = totalCharged - totalPaid,
            Entries = entries,
        };
    }

    public async Task<LeaseResponse?> CreateAsync(int portfolioId, CreateLeaseRequest request, CancellationToken ct = default)
    {
        // Verify the referenced property, unit, and tenant all live in the caller's portfolio.
        if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId, ct))
        {
            return null;
        }

        if (!await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId, request.PropertyId, ct))
        {
            return null;
        }

        if (!await _db.EnsureTenantInPortfolioAsync(portfolioId, request.TenantId, ct))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new Lease
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            TenantId = request.TenantId,
            LeaseNumber = request.LeaseNumber,
            Status = request.Status,
            StartDate = request.StartDate.ToUtc(),
            EndDate = request.EndDate.ToUtc(),
            MoveInDate = request.MoveInDate.ToUtc(),
            MoveOutDate = request.MoveOutDate.ToUtc(),
            MonthlyRent = request.MonthlyRent,
            SecurityDeposit = request.SecurityDeposit,
            LateFeeAmount = request.LateFeeAmount,
            RentDueDay = request.RentDueDay,
            Notes = request.Notes,
            ExtractedData = request.ExtractedData,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Leases.Add(entity);
        await _db.SaveChangesAsync(ct);

        // The generic Created twin already holds a full snapshot; add the legal change reason to it.
        await _audit.LogAsync(portfolioId, EntityType, entity.Id, AuditLogOperation.Created,
            changeReason: $"Lease {entity.LeaseNumber} created (status {entity.Status})", ct: ct);

        var response = LeaseResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<LeaseResponse?> UpdateAsync(int portfolioId, int id, UpdateLeaseRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Leases
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        // Full before-snapshot + the legally-relevant scalars, captured prior to mutation.
        var before = Snapshot(entity);
        var prevStatus = entity.Status;
        var prevRent = entity.MonthlyRent;
        var prevEnd = entity.EndDate;
        var prevDeposit = entity.SecurityDeposit;

        if (request.LeaseNumber != null) entity.LeaseNumber = request.LeaseNumber;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.StartDate.HasValue) entity.StartDate = request.StartDate.Value.ToUtc();
        if (request.EndDate.HasValue) entity.EndDate = request.EndDate.Value.ToUtc();
        if (request.MoveInDate.HasValue) entity.MoveInDate = request.MoveInDate.ToUtc();
        if (request.MoveOutDate.HasValue) entity.MoveOutDate = request.MoveOutDate.ToUtc();
        if (request.MonthlyRent.HasValue) entity.MonthlyRent = request.MonthlyRent.Value;
        if (request.SecurityDeposit.HasValue) entity.SecurityDeposit = request.SecurityDeposit.Value;
        if (request.LateFeeAmount.HasValue) entity.LateFeeAmount = request.LateFeeAmount.Value;
        if (request.RentDueDay.HasValue) entity.RentDueDay = request.RentDueDay.Value;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // Record the full before→after snapshot, summarizing the high-stakes field changes in the reason.
        var changes = new List<string>();
        if (entity.Status != prevStatus) changes.Add($"status {prevStatus}→{entity.Status}");
        if (entity.MonthlyRent != prevRent) changes.Add($"rent {prevRent:0.##}→{entity.MonthlyRent:0.##}");
        if (entity.EndDate != prevEnd) changes.Add($"end date {prevEnd:yyyy-MM-dd}→{entity.EndDate:yyyy-MM-dd}");
        if (entity.SecurityDeposit != prevDeposit) changes.Add($"deposit {prevDeposit:0.##}→{entity.SecurityDeposit:0.##}");
        var reason = changes.Count > 0
            ? $"Lease {entity.LeaseNumber}: {string.Join("; ", changes)}"
            : $"Lease {entity.LeaseNumber} details updated";
        await _audit.LogAsync(portfolioId, EntityType, entity.Id, AuditLogOperation.Updated,
            oldValues: before, newValues: Snapshot(entity), changeReason: reason, ct: ct);

        var response = LeaseResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Leases
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // Capture the full pre-termination snapshot before the soft-delete (the generic twin would
        // otherwise record only the DeletedAt change).
        var before = Snapshot(entity);

        entity.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(portfolioId, EntityType, id, AuditLogOperation.Deleted,
            oldValues: before, changeReason: $"Lease {entity.LeaseNumber} terminated", ct: ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    public async Task<LeaseDocumentResponse?> GenerateDocumentAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var lease = await _db.Leases
            .AsNoTracking()
            .Include(l => l.Tenant)
            .Include(l => l.Unit)
            .Include(l => l.Property)
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (lease == null)
        {
            return null;
        }

        var portfolio = await _db.Portfolios
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);

        var landlordName = !string.IsNullOrWhiteSpace(portfolio?.ManagementCompanyName)
            ? portfolio!.ManagementCompanyName
            : portfolio?.Name ?? "Landlord";

        var tenantName = lease.Tenant == null
            ? string.Empty
            : $"{lease.Tenant.FirstName} {lease.Tenant.LastName}".Trim();

        var property = lease.Property;
        var propertyAddress = property == null
            ? string.Empty
            : string.Join(", ", new[]
            {
                property.AddressLine1,
                property.AddressLine2,
                $"{property.City}, {property.State} {property.PostalCode}".Trim(),
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var data = new LeaseAgreementData
        {
            Lease = lease,
            LandlordName = landlordName,
            TenantName = tenantName,
            PropertyName = property?.Name ?? string.Empty,
            PropertyAddress = propertyAddress,
            UnitNumber = lease.Unit?.UnitNumber,
            State = property?.State ?? string.Empty,
            YearBuilt = property?.YearBuilt,
        };

        var pdfBytes = _pdf.Generate(data);

        // Write the blob first to get its key, then persist the StoredFile row; clean up the blob if the
        // row fails (mirrors the inspection-report storage pattern).
        var fileName = $"lease-{lease.Id}-agreement.pdf";
        string storageKey;
        await using (var ms = new MemoryStream(pdfBytes))
        {
            storageKey = await _storage.UploadAsync(ms, fileName, "application/pdf", ct);
        }

        var stored = new StoredFile
        {
            PortfolioId = portfolioId,
            FileName = fileName,
            FilePath = storageKey,
            ContentType = "application/pdf",
            FileSize = pdfBytes.Length,
            EntityType = EntityType,
            EntityId = lease.Id,
            UploadedAt = DateTime.UtcNow,
        };

        try
        {
            _db.StoredFiles.Add(stored);
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            try { await _storage.DeleteAsync(storageKey, ct); } catch { /* best-effort */ }
            throw;
        }

        return new LeaseDocumentResponse(
            stored.Id,
            lease.Id,
            stored.FileName,
            stored.FileSize,
            $"/api/v1/leases/{lease.Id}/document",
            stored.UploadedAt);
    }

    public async Task<(Stream Stream, string FileName, string ContentType)?> GetDocumentAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        // The lease must be in the caller's portfolio (IDOR guard) before we surface any document for it.
        var leaseExists = await _db.Leases
            .AnyAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (!leaseExists)
        {
            return null;
        }

        // Latest generated agreement for this lease, in this portfolio.
        var file = await _db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == portfolioId
                && f.EntityType == EntityType
                && f.EntityId == id
                && f.DeletedAt == null)
            .OrderByDescending(f => f.UploadedAt)
            .ThenByDescending(f => f.Id)
            .FirstOrDefaultAsync(ct);
        if (file == null)
        {
            return null;
        }

        Stream stream;
        try
        {
            stream = await _storage.DownloadAsync(file.FilePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lease agreement blob missing for lease {LeaseId} (file {FileId})", id, file.Id);
            return null;
        }

        return (stream, file.FileName, file.ContentType);
    }
}
