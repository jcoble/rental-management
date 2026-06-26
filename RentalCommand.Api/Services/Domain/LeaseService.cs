using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ILeaseService"/>
public class LeaseService : ILeaseService
{
    private const string EntityType = "Lease";
    private const string PaymentEntityType = "Payment";
    private const string SecurityDepositEntityType = "SecurityDeposit";
    private const string LeaseDepositHoldingNote = "Created from lease security deposit.";
    private const string GeneratedAgreementContentType = "application/pdf";

    // Allowed lease lifecycle transitions. A lease is a legal contract, so status may only move along
    // these edges — anything else (e.g. resurrecting a Terminated lease, or jumping Draft→Expired) is a
    // data-integrity defect and is rejected with a 400 before it can desync occupancy or orphan an
    // executed signature. A same→same move is always allowed (no-op) and is handled separately, so the
    // sets below list only genuine forward moves.
    //   Draft            → PendingSignature, Active, Void
    //   PendingSignature → Active, Draft, Void           (sign completes → Active; recall → Draft)
    //   Active           → NoticeGiven, Expired, Terminated
    //   NoticeGiven      → Active, Expired, Terminated    (Active = notice cancelled)
    //   Expired/Terminated/Void = terminal — no outbound edges (no reactivation)
    // NOTE: the e-sign "send" path (Draft/PendingSignature → PendingSignature) sets the status directly
    // on the entity in LeaseEsignService, NOT through UpdateAsync, so it is unaffected by this guard.
    private static readonly IReadOnlyDictionary<LeaseStatus, IReadOnlySet<LeaseStatus>> AllowedTransitions =
        new Dictionary<LeaseStatus, IReadOnlySet<LeaseStatus>>
        {
            [LeaseStatus.Draft] = new HashSet<LeaseStatus> { LeaseStatus.PendingSignature, LeaseStatus.Active, LeaseStatus.Void },
            [LeaseStatus.PendingSignature] = new HashSet<LeaseStatus> { LeaseStatus.Active, LeaseStatus.Draft, LeaseStatus.Void },
            [LeaseStatus.Active] = new HashSet<LeaseStatus> { LeaseStatus.NoticeGiven, LeaseStatus.Expired, LeaseStatus.Terminated },
            [LeaseStatus.NoticeGiven] = new HashSet<LeaseStatus> { LeaseStatus.Active, LeaseStatus.Expired, LeaseStatus.Terminated },
            [LeaseStatus.Expired] = new HashSet<LeaseStatus>(),
            [LeaseStatus.Terminated] = new HashSet<LeaseStatus>(),
            [LeaseStatus.Void] = new HashSet<LeaseStatus>(),
        };

    // A lease "occupies" its unit while it is Active or under NoticeGiven (tenant hasn't moved out yet).
    // Only these states conflict for the double-booking guard; a Draft/Pending/terminal lease holds no
    // unit. Mirrors the occupancy logic in SyncUnitOccupancyAsync.
    private static bool OccupiesUnit(LeaseStatus status)
        => status == LeaseStatus.Active || status == LeaseStatus.NoticeGiven;

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
        rentTrackingStartDate = l.RentTrackingStartDate,
        notes = l.Notes,
        tenantId = l.TenantId,
        propertyId = l.PropertyId,
        unitId = l.UnitId,
    });

    private static string FormatDateChange(DateTime? value)
        => value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "none";

    // Unit.Status is the canonical occupancy source ("Unit.Status everywhere, SQL-side"), so the lease
    // lifecycle owns keeping it correct. A lease that is Active occupies its unit; a unit under NoticeGiven
    // is still occupied (the tenant hasn't moved out yet), so only a genuine exit (Expired/Terminated/Void/
    // Draft/PendingSignature, or a soft-delete) frees the unit — and even then only when NO other Active
    // lease still references that unit. Called inside the same transaction as the lease save (before
    // SaveChanges) so the unit row participates in the same write.
    private async Task SyncUnitOccupancyAsync(int portfolioId, int unitId, int leaseId, LeaseStatus leaseStatus, CancellationToken ct)
    {
        // Units are scoped to a portfolio through their Property (Unit has no PortfolioId of its own).
        var unit = await _db.Units.FirstOrDefaultAsync(
            u => u.Id == unitId && u.Property!.PortfolioId == portfolioId, ct);
        if (unit == null)
        {
            return;
        }

        if (leaseStatus == LeaseStatus.Active)
        {
            unit.Status = UnitStatus.Occupied;
            return;
        }

        // The lease is not Active. NoticeGiven keeps the unit occupied until move-out, so it does NOT free
        // the unit here. For a real exit, only vacate when no OTHER lease still holds the unit Active.
        if (leaseStatus == LeaseStatus.NoticeGiven)
        {
            return;
        }

        var stillOccupied = await _db.Leases.AnyAsync(
            l => l.UnitId == unitId
                && l.PortfolioId == portfolioId
                && l.Id != leaseId
                && l.Status == LeaseStatus.Active,
            ct);

        if (!stillOccupied && unit.Status == UnitStatus.Occupied)
        {
            unit.Status = UnitStatus.Vacant;
        }
    }

    private static DateTime RentChargeGenerationStart(Lease lease)
    {
        var leaseStart = lease.StartDate.Date;
        var trackingStart = lease.RentTrackingStartDate?.Date;
        if (!trackingStart.HasValue || trackingStart.Value < leaseStart)
        {
            return leaseStart;
        }

        return trackingStart.Value;
    }

    private static DateTime? ResolveRentTrackingStartDate(
        DateTime leaseStart,
        RentTrackingStartMode mode,
        DateTime? requestedStartDate,
        DateTime today)
    {
        var leaseStartDate = leaseStart.Date;
        return mode switch
        {
            RentTrackingStartMode.BackfillFromLeaseStart => null,
            RentTrackingStartMode.ForwardOnly => MaxDate(leaseStartDate, today.Date),
            RentTrackingStartMode.CustomCutoffDate => requestedStartDate.HasValue
                ? MaxDate(leaseStartDate, requestedStartDate.Value.ToUtc().Date)
                : throw new DomainValidationException("Rent tracking cutoff date is required."),
            _ => throw new DomainValidationException("Rent tracking start mode is invalid."),
        };
    }

    private static DateTime MaxDate(DateTime left, DateTime right)
        => left >= right ? left : right;

    private async Task<IReadOnlyList<Payment>> EnsureRentChargesThroughTodayAsync(Lease lease, CancellationToken ct)
    {
        if (lease.Status != LeaseStatus.Active || lease.MonthlyRent <= 0)
        {
            return [];
        }

        var periods = RentChargeSchedule.GetDuePeriods(
            RentChargeGenerationStart(lease),
            lease.EndDate,
            lease.RentDueDay,
            DateTime.UtcNow.Date);
        if (periods.Count == 0)
        {
            return [];
        }

        var periodKeys = periods
            .Select(p => p.PeriodKey)
            .ToList();
        var existingPeriodKeys = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == lease.PortfolioId
                && p.LeaseId == lease.Id
                && p.PaymentType == PaymentType.Rent
                && p.PeriodKey != null
                && periodKeys.Contains(p.PeriodKey))
            .Select(p => p.PeriodKey!)
            .ToListAsync(ct);

        var existing = existingPeriodKeys.ToHashSet(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var created = periods
            .Where(p => !existing.Contains(p.PeriodKey))
            .Select(period => new Payment
            {
                PortfolioId = lease.PortfolioId,
                LeaseId = lease.Id,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Scheduled,
                Amount = lease.MonthlyRent,
                DueDate = period.DueDate,
                PeriodKey = period.PeriodKey,
                CreatedAt = now,
                UpdatedAt = now,
            })
            .ToList();

        if (created.Count == 0)
        {
            return [];
        }

        _db.Payments.AddRange(created);
        await _db.SaveChangesAsync(ct);

        foreach (var payment in created)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                lease.PortfolioId,
                PaymentEntityType,
                payment.Id,
                PaymentResponse.FromEntity(payment),
                ct);
        }

        return created;
    }

    private async Task<SecurityDepositHolding?> EnsureSecurityDepositHoldingAsync(Lease lease, CancellationToken ct)
    {
        if (!OccupiesUnit(lease.Status) || lease.SecurityDeposit <= 0m)
        {
            return null;
        }

        var exists = await _db.SecurityDepositHoldings
            .AsNoTracking()
            .AnyAsync(h => h.PortfolioId == lease.PortfolioId && h.LeaseId == lease.Id, ct);
        if (exists)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var holding = new SecurityDepositHolding
        {
            PortfolioId = lease.PortfolioId,
            LeaseId = lease.Id,
            Amount = lease.SecurityDeposit,
            Status = SecurityDepositStatus.Held,
            HeldAt = now,
            DeductionsJson = "[]",
            Notes = LeaseDepositHoldingNote,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.SecurityDepositHoldings.Add(holding);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            lease.PortfolioId,
            SecurityDepositEntityType,
            holding.Id,
            AuditLogOperation.Created,
            newValues: JsonSerializer.Serialize(new
            {
                leaseId = holding.LeaseId,
                amount = holding.Amount,
                status = holding.Status.ToString(),
                heldAt = holding.HeldAt,
                notes = holding.Notes,
            }),
            changeReason: $"Security deposit holding created from lease {lease.LeaseNumber} (${holding.Amount:0.##})",
            ct: ct);

        return holding;
    }

    // Reject a status move that isn't on the lifecycle graph. A same→same move is always allowed (a PATCH
    // that re-sends the current status, or that touches only rent/dates, must not be blocked). Throws a
    // 400 DomainValidationException with a message naming the illegal edge.
    private static void EnsureTransitionAllowed(LeaseStatus from, LeaseStatus to)
    {
        if (from == to)
        {
            return;
        }

        if (!AllowedTransitions.TryGetValue(from, out var allowed) || !allowed.Contains(to))
        {
            throw new DomainValidationException(
                $"A lease cannot move from {from} to {to}.");
        }
    }

    // Reject booking/activating a unit that another lease already holds over an overlapping date range.
    // Scoped to the SAME portfolio + unit, excludes the lease being edited and any soft-deleted lease
    // (the global query filter already drops soft-deleted rows), and only conflicts with a lease that
    // OCCUPIES the unit (Active or NoticeGiven — a tenant in place). Half-open overlap: [s1,e1) and
    // [s2,e2) overlap iff s1 < e2 AND s2 < e1. Runs as a single EF-translated EXISTS query (SQL-side).
    // Throws a 409 DomainValidationException on conflict.
    private async Task EnsureNoOverlappingActiveLeaseAsync(
        int portfolioId, int unitId, int leaseId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var conflict = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId
                && l.UnitId == unitId
                && l.Id != leaseId
                && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven)
                && l.StartDate < endUtc
                && startUtc < l.EndDate)
            .Select(l => l.LeaseNumber)
            .FirstOrDefaultAsync(ct);

        if (conflict != null)
        {
            throw new DomainValidationException(
                $"This unit already has an active lease ({conflict}) overlapping these dates. "
                    + "End or move that lease before activating another for the same dates.",
                statusCode: 409);
        }
    }

    // Validate the date range BEFORE the DB CHECK constraint (CK_Lease_StartBeforeEnd) is hit, so the
    // client gets a clean 400 instead of a constraint-violation 500. The DB constraint remains the
    // backstop. Compares the UTC-normalized values that will actually be persisted.
    private static void EnsureValidDateRange(DateTime startUtc, DateTime endUtc)
    {
        if (startUtc >= endUtc)
        {
            throw new DomainValidationException(
                "The lease start date must be before its end date.");
        }
    }

    public async Task<IReadOnlyList<LeaseResponse>> ListAsync(int portfolioId, int? tenantId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, ToLeaseListQuery(query, tenantId, propertyId), ct);
        return page.Items;
    }

    public async Task<LeaseListResponse> ListPageAsync(int portfolioId, LeaseListQuery query, CancellationToken ct = default)
    {
        var q = _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId);

        if (query.TenantId.HasValue)
        {
            q = q.Where(l => l.TenantId == query.TenantId.Value);
        }

        if (query.PropertyId.HasValue)
        {
            q = q.Where(l => l.PropertyId == query.PropertyId.Value);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(l => l.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(l =>
                EF.Functions.ILike(l.LeaseNumber, $"%{term}%") ||
                EF.Functions.ILike(l.Tenant!.FirstName, $"%{term}%") ||
                EF.Functions.ILike(l.Tenant.LastName, $"%{term}%") ||
                EF.Functions.ILike(l.Tenant.FirstName + " " + l.Tenant.LastName, $"%{term}%") ||
                EF.Functions.ILike(l.Property!.Name, $"%{term}%") ||
                EF.Functions.ILike(l.Unit!.UnitNumber, $"%{term}%"));
        }

        q = query.SortField switch
        {
            "leasenumber" => query.SortDescending ? q.OrderByDescending(l => l.LeaseNumber) : q.OrderBy(l => l.LeaseNumber),
            "tenantname" => query.SortDescending ? q.OrderByDescending(l => l.Tenant!.LastName).ThenByDescending(l => l.Tenant!.FirstName) : q.OrderBy(l => l.Tenant!.LastName).ThenBy(l => l.Tenant!.FirstName),
            "propertyname" => query.SortDescending ? q.OrderByDescending(l => l.Property!.Name) : q.OrderBy(l => l.Property!.Name),
            "unitnumber" => query.SortDescending ? q.OrderByDescending(l => l.Unit!.UnitNumber) : q.OrderBy(l => l.Unit!.UnitNumber),
            "status" => query.SortDescending ? q.OrderByDescending(l => l.Status) : q.OrderBy(l => l.Status),
            "startdate" => query.SortDescending ? q.OrderByDescending(l => l.StartDate) : q.OrderBy(l => l.StartDate),
            "enddate" => query.SortDescending ? q.OrderByDescending(l => l.EndDate) : q.OrderBy(l => l.EndDate),
            "monthlyrent" => query.SortDescending ? q.OrderByDescending(l => l.MonthlyRent) : q.OrderBy(l => l.MonthlyRent),
            "createdat" => query.SortDescending ? q.OrderByDescending(l => l.CreatedAt) : q.OrderBy(l => l.CreatedAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(l => l.UpdatedAt) : q.OrderBy(l => l.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(l => l.CreatedAt) : q.OrderBy(l => l.CreatedAt),
        };

        var totalCount = await q.CountAsync(ct);

        var rows = await q
            .Select(l => new ProjectedLease(
                l,
                l.Tenant == null ? null : (l.Tenant.FirstName + " " + l.Tenant.LastName).Trim(),
                l.Unit == null ? null : l.Unit.UnitNumber,
                l.Property == null ? null : l.Property.Name))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new LeaseListResponse
        {
            Items = rows.Select(ToResponse).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static LeaseListQuery ToLeaseListQuery(ListQuery query, int? tenantId, int? propertyId) => new()
    {
        Skip = query.Skip,
        Take = query.Take,
        Search = query.Search,
        Sort = query.Sort,
        TenantId = tenantId,
        PropertyId = propertyId,
    };

    private sealed record ProjectedLease(Lease Lease, string? TenantName, string? UnitNumber, string? PropertyName);

    private static LeaseResponse ToResponse(ProjectedLease row)
    {
        var response = LeaseResponse.FromEntity(row.Lease);
        response.TenantName = row.TenantName;
        response.UnitNumber = row.UnitNumber;
        response.PropertyName = row.PropertyName;
        return response;
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
        var scan = await FindLatestAvailableLeaseSourceFileAsync(portfolioId, id, ct);
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

        // The ledger ROWS are needed to render each transaction line, so the per-payment projection is
        // loaded for display. The TOTALS, however, are computed SQL-side over the whole payment set (see
        // below) rather than re-summed from these rows — keeping the headline figures a DB aggregate even
        // if the page ever paginates the rows.
        var paymentsQuery = _db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == id && p.PortfolioId == portfolioId);

        var payments = await paymentsQuery
            .Select(p => new
            {
                p.Id,
                p.PaymentType,
                p.Status,
                p.Amount,
                p.DueDate,
                p.PaidDate,
                p.Method,
                LedgerDate = p.PaidDate ?? p.DueDate,
            })
            .OrderByDescending(p => p.LedgerDate)
            .ThenByDescending(p => p.Id)
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
                    Date = p.LedgerDate,
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
            .ToList();

        // Each payment row is a billed charge (rent, fee, deposit). "Charged" is every real charge;
        // "Paid" is what's been collected. Balance (charged − paid) is exactly what's still owed.
        // Waived/Failed/Refunded rows aren't money owed and weren't collected, so they're excluded
        // from both totals and net to zero in the balance. Both totals are computed SQL-side as a single
        // grouped-by-status aggregate (SUM per status in the database); only the handful of status rows
        // come back, and the relevant statuses are summed from that tiny grouped result.
        var statusTotals = await paymentsQuery
            .GroupBy(p => p.Status)
            .Select(g => new
            {
                Status = g.Key,
                Total = g.Sum(p => p.Amount),
                // Cash collected against this status' charges: only a Partial carries a split AmountPaid.
                Collected = g.Sum(p => p.AmountPaid ?? 0m),
            })
            .ToListAsync(ct);

        // Charged = every real charge at its full billed Amount (Waived/Failed/Refunded excluded).
        var totalCharged = statusTotals
            .Where(s => s.Status is PaymentStatus.Scheduled or PaymentStatus.Partial
                or PaymentStatus.Late or PaymentStatus.Paid)
            .Sum(s => s.Total);
        // Paid = the full Amount of Paid charges plus the collected-so-far of Partial charges; the
        // Partial remainder stays in the balance (Balance = Charged − Paid). The grouped sums above are
        // already DB-side aggregates over the tiny per-status result.
        var totalPaid = statusTotals
            .Where(s => s.Status == PaymentStatus.Paid)
            .Sum(s => s.Total)
            + statusTotals
                .Where(s => s.Status == PaymentStatus.Partial)
                .Sum(s => s.Collected);

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

        var startUtc = request.StartDate.ToUtc();
        var endUtc = request.EndDate.ToUtc();

        // Clean 400 for an inverted range before the DB CHECK constraint turns it into a raw 500.
        EnsureValidDateRange(startUtc, endUtc);

        // A new lease created already-occupying its unit (Active/NoticeGiven) must not double-book a unit
        // that another lease already holds over an overlapping range. A Draft/Pending lease books nothing,
        // so it skips the check. Id 0 (unsaved) never matches an existing row.
        if (OccupiesUnit(request.Status))
        {
            await EnsureNoOverlappingActiveLeaseAsync(portfolioId, request.UnitId, 0, startUtc, endUtc, ct);
        }

        var now = DateTime.UtcNow;
        var rentTrackingStartDate = request.Status == LeaseStatus.Active
            ? ResolveRentTrackingStartDate(startUtc, request.RentTrackingStartMode, request.RentTrackingStartDate, now.Date)
            : null;
        var entity = new Lease
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            TenantId = request.TenantId,
            LeaseNumber = request.LeaseNumber,
            Status = request.Status,
            StartDate = startUtc,
            EndDate = endUtc,
            MoveInDate = request.MoveInDate.ToUtc(),
            MoveOutDate = request.MoveOutDate.ToUtc(),
            MonthlyRent = request.MonthlyRent,
            SecurityDeposit = request.SecurityDeposit,
            LateFeeAmount = request.LateFeeAmount,
            RentDueDay = request.RentDueDay,
            RentTrackingStartDate = rentTrackingStartDate,
            Notes = request.Notes,
            ExtractedData = request.ExtractedData,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Leases.Add(entity);

        // A lease created Active immediately occupies its unit. (A non-Active new lease never frees a unit
        // here — that only happens when an existing Active lease exits.) Tracked unit is saved in the same
        // transaction below.
        if (entity.Status == LeaseStatus.Active)
        {
            await SyncUnitOccupancyAsync(portfolioId, entity.UnitId, entity.Id, entity.Status, ct);
        }

        await _db.SaveChangesAsync(ct);

        // The generic Created twin already holds a full snapshot; add the legal change reason to it.
        await _audit.LogAsync(portfolioId, EntityType, entity.Id, AuditLogOperation.Created,
            changeReason: $"Lease {entity.LeaseNumber} created (status {entity.Status})", ct: ct);

        await EnsureSecurityDepositHoldingAsync(entity, ct);
        await EnsureRentChargesThroughTodayAsync(entity, ct);

        var response = LeaseResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<LeaseResponse?> UpdateAsync(int portfolioId, int id, UpdateLeaseRequest request, CancellationToken ct = default)
    {
        // Eager-load the label navigations so the PATCH response carries TenantName/PropertyName/UnitNumber
        // (the grid binds these) instead of flashing "-" until the next list refetch.
        var entity = await _db.Leases
            .Include(l => l.Tenant)
            .Include(l => l.Property)
            .Include(l => l.Unit)
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
        var prevMoveOutDate = entity.MoveOutDate;
        var prevRentTrackingStartDate = entity.RentTrackingStartDate;
        var prevNotes = entity.Notes;

        // Resolve the post-update status + date range up front (request value when supplied, else current)
        // so the integrity guards reason about what will ACTUALLY be persisted.
        var newStatus = request.Status ?? entity.Status;
        var newStartUtc = request.StartDate?.ToUtc() ?? entity.StartDate;
        var newEndUtc = request.EndDate?.ToUtc() ?? entity.EndDate;

        // 1. State machine: a status move must be a legal lifecycle edge (no-op same→same allowed).
        EnsureTransitionAllowed(prevStatus, newStatus);

        // 2. Date range: clean 400 before the DB CHECK constraint would 500.
        EnsureValidDateRange(newStartUtc, newEndUtc);

        // 3. Double-booking: when this edit ACTIVATES the lease (a genuine new occupation of the unit —
        //    Draft/Pending/terminal → Active/NoticeGiven), reject if another occupying lease already holds
        //    the unit over an overlapping range. Editing an already-occupying lease (e.g. a rent change on
        //    an Active lease) does NOT re-run the check, so it never trips on pre-existing data; the
        //    excluded-self clause also keeps a no-op safe.
        if (OccupiesUnit(newStatus) && !OccupiesUnit(prevStatus))
        {
            await EnsureNoOverlappingActiveLeaseAsync(portfolioId, entity.UnitId, entity.Id, newStartUtc, newEndUtc, ct);
        }

        if (request.LeaseNumber != null) entity.LeaseNumber = request.LeaseNumber;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.StartDate.HasValue) entity.StartDate = request.StartDate.Value.ToUtc();
        if (request.EndDate.HasValue) entity.EndDate = request.EndDate.Value.ToUtc();
        if (request.MoveInDate.HasValue) entity.MoveInDate = request.MoveInDate.ToUtc();
        if (prevStatus == LeaseStatus.NoticeGiven && request.Status == LeaseStatus.Active)
        {
            entity.MoveOutDate = null;
        }
        else if (request.MoveOutDate.HasValue)
        {
            entity.MoveOutDate = request.MoveOutDate.ToUtc();
        }
        if (request.MonthlyRent.HasValue) entity.MonthlyRent = request.MonthlyRent.Value;
        if (request.SecurityDeposit.HasValue) entity.SecurityDeposit = request.SecurityDeposit.Value;
        if (request.LateFeeAmount.HasValue) entity.LateFeeAmount = request.LateFeeAmount.Value;
        if (request.RentDueDay.HasValue) entity.RentDueDay = request.RentDueDay.Value;
        if (request.RentTrackingStartMode.HasValue)
        {
            entity.RentTrackingStartDate = newStatus == LeaseStatus.Active
                ? ResolveRentTrackingStartDate(entity.StartDate, request.RentTrackingStartMode.Value, request.RentTrackingStartDate, DateTime.UtcNow.Date)
                : null;
        }
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        // Keep the canonical Unit.Status in step with the lease lifecycle whenever the status moves:
        // activating occupies the unit; a real exit frees it (unless another Active lease still holds it).
        // Tracked unit is saved in the same transaction below.
        if (entity.Status != prevStatus)
        {
            await SyncUnitOccupancyAsync(portfolioId, entity.UnitId, entity.Id, entity.Status, ct);
        }

        await _db.SaveChangesAsync(ct);

        // Record the full before→after snapshot, summarizing the high-stakes field changes in the reason.
        var changes = new List<string>();
        if (entity.Status != prevStatus) changes.Add($"status {prevStatus}→{entity.Status}");
        if (entity.MonthlyRent != prevRent) changes.Add($"rent {prevRent:0.##}→{entity.MonthlyRent:0.##}");
        if (entity.EndDate != prevEnd) changes.Add($"end date {prevEnd:yyyy-MM-dd}→{entity.EndDate:yyyy-MM-dd}");
        if (entity.SecurityDeposit != prevDeposit) changes.Add($"deposit {prevDeposit:0.##}→{entity.SecurityDeposit:0.##}");
        if (entity.MoveOutDate != prevMoveOutDate) changes.Add($"move-out date {FormatDateChange(prevMoveOutDate)}→{FormatDateChange(entity.MoveOutDate)}");
        if (entity.RentTrackingStartDate != prevRentTrackingStartDate)
        {
            changes.Add($"rent tracking start {FormatDateChange(prevRentTrackingStartDate)}→{FormatDateChange(entity.RentTrackingStartDate)}");
        }
        if (!string.Equals(entity.Notes, prevNotes, StringComparison.Ordinal)) changes.Add("notes updated");
        var reason = changes.Count > 0
            ? $"Lease {entity.LeaseNumber}: {string.Join("; ", changes)}"
            : $"Lease {entity.LeaseNumber} details updated";
        await _audit.LogAsync(portfolioId, EntityType, entity.Id, AuditLogOperation.Updated,
            oldValues: before, newValues: Snapshot(entity), changeReason: reason, ct: ct);

        await EnsureSecurityDepositHoldingAsync(entity, ct);

        if ((prevStatus != LeaseStatus.Active && entity.Status == LeaseStatus.Active)
            || (entity.Status == LeaseStatus.Active && request.RentTrackingStartMode.HasValue))
        {
            await EnsureRentChargesThroughTodayAsync(entity, ct);
        }

        var response = LeaseResponse.FromEntity(entity, includeNavigations: true);
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

        // Terminating (soft-deleting) a lease is a real exit: free the unit unless another Active lease
        // still holds it. The soft-deleted lease is excluded both by the explicit Id guard and by the
        // global soft-delete query filter, so it never counts itself as the occupant. Tracked unit saves
        // in the same transaction below.
        await SyncUnitOccupancyAsync(portfolioId, entity.UnitId, entity.Id, LeaseStatus.Terminated, ct);

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

    public async Task<LeaseDocumentStatusResponse?> GetDocumentStatusAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var leaseExists = await _db.Leases
            .AsNoTracking()
            .AnyAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (!leaseExists)
        {
            return null;
        }

        var agreementFileName = GeneratedAgreementFileName(id);
        var file = await _db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == portfolioId
                && f.EntityType == EntityType
                && f.EntityId == id
                && f.FileName == agreementFileName
                && f.ContentType == GeneratedAgreementContentType
                && f.DeletedAt == null)
            .OrderByDescending(f => f.UploadedAt)
            .ThenByDescending(f => f.Id)
            .Select(f => new
            {
                f.Id,
                f.FileName,
                f.FileSize,
                f.UploadedAt
            })
            .FirstOrDefaultAsync(ct);

        return new LeaseDocumentStatusResponse(
            id,
            file is not null,
            file?.Id,
            file?.FileName,
            file?.FileSize,
            file is null ? null : $"/api/v1/leases/{id}/document",
            file?.UploadedAt);
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
        var agreementFileName = GeneratedAgreementFileName(id);
        var file = await _db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == portfolioId
                && f.EntityType == EntityType
                && f.EntityId == id
                && f.FileName == agreementFileName
                && f.ContentType == GeneratedAgreementContentType
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

    private async Task<StoredFile?> FindLatestAvailableLeaseSourceFileAsync(
        int portfolioId,
        int leaseId,
        CancellationToken ct)
    {
        var agreementFileName = GeneratedAgreementFileName(leaseId);
        var signedFileName = SignedAgreementFileName(leaseId);
        var storedFile = await _db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == portfolioId
                && f.EntityType == EntityType
                && f.EntityId == leaseId
                && f.FileName != agreementFileName
                && f.FileName != signedFileName
                && f.DeletedAt == null)
            .OrderByDescending(f => f.UploadedAt)
            .ThenByDescending(f => f.Id)
            .FirstOrDefaultAsync(ct);
        if (storedFile is null)
        {
            return null;
        }

        try
        {
            await using var stream = await _storage.DownloadAsync(storedFile.FilePath, ct);
            return storedFile;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string GeneratedAgreementFileName(int leaseId) => $"lease-{leaseId}-agreement.pdf";
    private static string SignedAgreementFileName(int leaseId) => $"lease-{leaseId}-signed.pdf";
}
