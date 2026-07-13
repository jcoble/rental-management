using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Services;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ILeaseService"/>
public class LeaseService : ILeaseService
{
    private const string EntityType = "Lease";
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

    private async Task<string> ResolveLeaseNumberAsync(
        int portfolioId,
        string? requestedLeaseNumber,
        DateTime startUtc,
        CancellationToken ct)
    {
        var trimmed = requestedLeaseNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        var prefix = $"L-{startUtc.Year}-";
        var existingForYear = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && EF.Functions.Like(l.LeaseNumber, prefix + "%"))
            .CountAsync(ct);

        for (var next = existingForYear + 1; ; next++)
        {
            var candidate = $"{prefix}{next:000}";
            var exists = await _db.Leases
                .AsNoTracking()
                .AnyAsync(l => l.PortfolioId == portfolioId && l.LeaseNumber == candidate, ct);
            if (!exists)
            {
                return candidate;
            }
        }
    }

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IFileStorage _storage;
    private readonly ILeaseAgreementPdfGenerator _pdf;
    private readonly ILeaseAgreementRenderer? _agreementRenderer;
    private readonly IAuditTrailService _audit;
    private readonly ILogger<LeaseService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IAppTimeZoneProvider _tz;
    private readonly ITenantPortalProvisioningService? _tenantPortalProvisioning;

    public LeaseService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IFileStorage storage,
        ILeaseAgreementPdfGenerator pdf,
        IAuditTrailService audit,
        ILogger<LeaseService> logger,
        TimeProvider timeProvider,
        ILeaseAgreementRenderer? agreementRenderer = null,
        IAppTimeZoneProvider? tz = null,
        ITenantPortalProvisioningService? tenantPortalProvisioning = null)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _storage = storage;
        _pdf = pdf;
        _agreementRenderer = agreementRenderer;
        _audit = audit;
        _logger = logger;
        _timeProvider = timeProvider;
        _tz = tz ?? UtcAppTimeZoneProvider.Instance;
        _tenantPortalProvisioning = tenantPortalProvisioning;
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
        tenantIds = l.LeaseTenants
            .OrderByDescending(lt => lt.IsPrimary)
            .ThenBy(lt => lt.Id)
            .Select(lt => lt.TenantId)
            .ToList(),
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

        if (OccupiesUnit(leaseStatus))
        {
            unit.Status = UnitStatus.Occupied;
            return;
        }

        // For a real exit, only vacate when no OTHER lease still holds the unit.
        var stillOccupied = await _db.Leases.AnyAsync(
            l => l.UnitId == unitId
                && l.PortfolioId == portfolioId
                && l.Id != leaseId
                && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven),
            ct);

        if (!stillOccupied && unit.Status == UnitStatus.Occupied)
        {
            unit.Status = UnitStatus.Vacant;
        }
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
            RentTrackingStartMode.OpeningBalanceOnly => MaxDate(leaseStartDate, today.Date),
            _ => throw new DomainValidationException("Rent tracking start mode is invalid."),
        };
    }

    private static DateTime MaxDate(DateTime left, DateTime right)
        => left >= right ? left : right;

    private sealed class UtcAppTimeZoneProvider : IAppTimeZoneProvider
    {
        public static readonly UtcAppTimeZoneProvider Instance = new();
        public TimeZoneInfo BusinessTimeZone => TimeZoneInfo.Utc;
    }

    private static bool HasOpeningBalanceRequest(decimal? amount, DateTime? asOfDate, string? note)
        => amount.HasValue || asOfDate.HasValue || note is not null;

    private static void EnsureOpeningBalanceRequestIsValid(
        RentTrackingStartMode mode,
        decimal? amount,
        DateTime? asOfDate,
        string? note)
    {
        if (mode != RentTrackingStartMode.OpeningBalanceOnly
            && !HasOpeningBalanceRequest(amount, asOfDate, note))
        {
            return;
        }

        throw new DomainValidationException(
            "Opening balances are posted to the Tenant account during Prepare move-in. "
            + "The legacy lease endpoint no longer creates or edits financial records.",
            statusCode: 409);
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

    private async Task EnsureUnitAvailableForOccupyingLeaseAsync(
        int portfolioId, int unitId, CancellationToken ct)
    {
        var unit = await _db.Units
            .AsNoTracking()
            .Where(u => u.Id == unitId && u.Property != null && u.Property.PortfolioId == portfolioId)
            .Select(u => new { u.Status })
            .FirstOrDefaultAsync(ct);

        if (unit == null)
        {
            return;
        }

        if (unit.Status != UnitStatus.Vacant)
        {
            throw new DomainValidationException(
                "Choose a vacant unit before activating this lease.",
                statusCode: 409);
        }
    }

    private async Task LockUnitForLeaseMutationAsync(int unitId, CancellationToken ct)
    {
        // PostgreSQL row locks serialize create/activation decisions for the same unit. Locking lets
        // the second request run the overlap and availability checks after the first commits, while
        // still allowing intentional adjacent, non-overlapping active lease terms.
        if (!_db.Database.IsNpgsql())
        {
            return;
        }

        var currentTransaction = _db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A unit lease lock requires an active database transaction.");
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.Transaction = currentTransaction.GetDbTransaction();
        command.CommandText = "SELECT 1 FROM \"Units\" WHERE \"Id\" = @unitId FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "unitId";
        parameter.Value = unitId;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(ct);
    }

    private async Task EnsureTenantsAvailableForOccupyingLeaseAsync(
        int portfolioId, int leaseId, IReadOnlyList<int> tenantIds, CancellationToken ct)
    {
        var conflict = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId
                && l.Id != leaseId
                && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven)
                && (tenantIds.Contains(l.TenantId) ||
                    l.LeaseTenants.Any(lt => tenantIds.Contains(lt.TenantId))))
            .Select(l => l.LeaseNumber)
            .FirstOrDefaultAsync(ct);

        if (conflict != null)
        {
            throw new DomainValidationException(
                $"One of the selected tenants is already on active lease {conflict}.",
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

    private static IReadOnlyList<int> NormalizeTenantIds(int? tenantId, IReadOnlyList<int>? tenantIds)
    {
        var result = new List<int>();
        if (tenantIds is { Count: > 0 })
        {
            foreach (var id in tenantIds)
            {
                if (id > 0 && !result.Contains(id))
                {
                    result.Add(id);
                }
            }
        }

        if (result.Count == 0 && tenantId is > 0)
        {
            result.Add(tenantId.Value);
        }

        return result;
    }

    private async Task<bool> AreTenantsInPortfolioAsync(int portfolioId, IReadOnlyList<int> tenantIds, CancellationToken ct)
    {
        if (tenantIds.Count == 0)
        {
            throw new DomainValidationException("At least one tenant is required for a lease.");
        }

        var matched = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && tenantIds.Contains(t.Id))
            .CountAsync(ct);

        return matched == tenantIds.Count;
    }

    private static void SetLeaseTenantMemberships(Lease lease, IReadOnlyList<int> tenantIds, DateTime now)
    {
        lease.TenantId = tenantIds[0];
        var selected = tenantIds.ToHashSet();
        for (var i = lease.LeaseTenants.Count - 1; i >= 0; i--)
        {
            if (!selected.Contains(lease.LeaseTenants[i].TenantId))
            {
                lease.LeaseTenants.RemoveAt(i);
            }
        }

        for (var index = 0; index < tenantIds.Count; index++)
        {
            var tenantId = tenantIds[index];
            var membership = lease.LeaseTenants.FirstOrDefault(lt => lt.TenantId == tenantId);
            if (membership is null)
            {
                lease.LeaseTenants.Add(new LeaseTenant
                {
                    PortfolioId = lease.PortfolioId,
                    LeaseId = lease.Id,
                    TenantId = tenantId,
                    IsPrimary = index == 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                continue;
            }

            membership.IsPrimary = index == 0;
            membership.UpdatedAt = now;
        }
    }

    private static IReadOnlyList<int> CurrentTenantIds(Lease lease)
    {
        var result = new List<int>();
        if (lease.TenantId > 0)
        {
            result.Add(lease.TenantId);
        }

        foreach (var leaseTenant in lease.LeaseTenants.OrderByDescending(lt => lt.IsPrimary).ThenBy(lt => lt.Id))
        {
            if (leaseTenant.TenantId > 0 && !result.Contains(leaseTenant.TenantId))
            {
                result.Add(leaseTenant.TenantId);
            }
        }

        return result;
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
            q = q.Where(l => l.TenantId == query.TenantId.Value
                || l.LeaseTenants.Any(lt => lt.TenantId == query.TenantId.Value));
        }

        if (query.PropertyId.HasValue)
        {
            q = q.Where(l => l.PropertyId == query.PropertyId.Value);
        }

        if (query.UnitId.HasValue)
        {
            q = q.Where(l => l.UnitId == query.UnitId.Value);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(l => l.Status == query.Status.Value);
        }

        if (query.StartFrom.HasValue)
        {
            var startFrom = query.StartFrom.Value.ToUtc();
            q = q.Where(l => l.StartDate >= startFrom);
        }

        if (query.StartTo.HasValue)
        {
            var startToExclusive = ToExclusiveUpperBound(query.StartTo.Value);
            q = q.Where(l => l.StartDate < startToExclusive);
        }

        if (query.EndFrom.HasValue)
        {
            var endFrom = query.EndFrom.Value.ToUtc();
            q = q.Where(l => l.EndDate >= endFrom);
        }

        if (query.EndTo.HasValue)
        {
            var endToExclusive = ToExclusiveUpperBound(query.EndTo.Value);
            q = q.Where(l => l.EndDate < endToExclusive);
        }

        if (query.ActiveOn.HasValue)
        {
            var activeOn = query.ActiveOn.Value.ToUtc();
            q = q.Where(l => l.StartDate <= activeOn && l.EndDate >= activeOn);
        }

        if (query.ActiveFrom.HasValue)
        {
            var activeFrom = query.ActiveFrom.Value.ToUtc();
            q = q.Where(l => l.EndDate >= activeFrom);
        }

        if (query.ActiveTo.HasValue)
        {
            var activeToExclusive = ToExclusiveUpperBound(query.ActiveTo.Value);
            q = q.Where(l => l.StartDate < activeToExclusive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(l =>
                EF.Functions.ILike(l.LeaseNumber, $"%{term}%") ||
                EF.Functions.ILike(l.Tenant!.FirstName, $"%{term}%") ||
                EF.Functions.ILike(l.Tenant.LastName, $"%{term}%") ||
                EF.Functions.ILike(l.Tenant.FirstName + " " + l.Tenant.LastName, $"%{term}%") ||
                l.LeaseTenants.Any(lt =>
                    EF.Functions.ILike(lt.Tenant!.FirstName, $"%{term}%") ||
                    EF.Functions.ILike(lt.Tenant.LastName, $"%{term}%") ||
                    EF.Functions.ILike(lt.Tenant.FirstName + " " + lt.Tenant.LastName, $"%{term}%")) ||
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

    private static LeaseListQuery ToLeaseListQuery(ListQuery query, int? tenantId, int? propertyId)
    {
        var leaseQuery = query as LeaseListQuery;
        return new LeaseListQuery
        {
            Skip = query.Skip,
            Take = query.Take,
            Search = query.Search,
            Sort = query.Sort,
            TenantId = tenantId ?? leaseQuery?.TenantId,
            PropertyId = propertyId ?? leaseQuery?.PropertyId,
            UnitId = leaseQuery?.UnitId,
            Status = leaseQuery?.Status,
            StartFrom = leaseQuery?.StartFrom,
            StartTo = leaseQuery?.StartTo,
            EndFrom = leaseQuery?.EndFrom,
            EndTo = leaseQuery?.EndTo,
            ActiveOn = leaseQuery?.ActiveOn,
            ActiveFrom = leaseQuery?.ActiveFrom,
            ActiveTo = leaseQuery?.ActiveTo,
        };
    }

    private static DateTime ToExclusiveUpperBound(DateTime value)
    {
        var utc = value.ToUtc();
        return value.TimeOfDay == TimeSpan.Zero ? utc.AddDays(1) : utc;
    }

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
            .Include(l => l.LeaseTenants)
                .ThenInclude(lt => lt.Tenant)
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

    private const int DefaultLedgerPageSize = 50;
    private const int MaxLedgerPageSize = 200;

    public async Task<LeaseLedgerResponse?> GetLedgerAsync(
        int portfolioId, int leaseManagementId, int? restrictToTenantId = null, int skip = 0, int? take = null, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        var pageSize = Math.Clamp(take ?? DefaultLedgerPageSize, 1, MaxLedgerPageSize);

        var header = await BuildCanonicalLedgerHeaderQuery(
                portfolioId, leaseManagementId, restrictToTenantId)
            .SingleOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }

        var ledgerRows = await BuildCanonicalLedgerEntriesQuery(portfolioId, header.TenantAccountId)
            .OrderByDescending(entry => entry.EffectiveOn)
            .ThenByDescending(entry => entry.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);

        var opening = await _db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.TenantAccountId == header.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.OpeningBalance)
            .OrderBy(entry => entry.EffectiveOn)
            .ThenBy(entry => entry.Id)
            .Select(entry => new CanonicalLedgerEntryReadRow
            {
                Id = entry.Id,
                TenantAccountId = entry.TenantAccountId,
                EntryType = entry.EntryType,
                Direction = entry.Direction,
                Amount = entry.Amount,
                EffectiveOn = entry.EffectiveOn,
                DueOn = entry.DueOn,
                Description = entry.Description,
                PaymentMethodSummary = entry.ProviderPaymentAttempt != null
                    ? entry.ProviderPaymentAttempt.PaymentMethodSummary
                    : null,
                LeaseAgreementBaseRent = entry.LeaseAgreement != null
                    ? entry.LeaseAgreement.BaseRentAmount
                    : null,
            })
            .FirstOrDefaultAsync(ct);

        return new LeaseLedgerResponse
        {
            LeaseManagementId = header.LeaseManagementId,
            TenantAccountId = header.TenantAccountId,
            AccountNumber = header.AccountNumber,
            TenantName = string.IsNullOrWhiteSpace(header.TenantName) ? "Tenant" : header.TenantName,
            PropertyName = header.PropertyName,
            TotalCharged = header.TotalDebits,
            TotalPaid = header.TotalCredits,
            Balance = header.ReceivableBalance,
            PastDueCount = header.PastDueCount,
            Opening = opening is null ? null : ToLedgerResponse(opening, header, "Opening"),
            Entries = ledgerRows.Select(entry => ToLedgerResponse(entry, header)).ToList(),
            TotalCount = header.TotalEntryCount,
            Skip = skip,
            Take = pageSize,
        };
    }

    internal IQueryable<CanonicalLedgerHeaderReadRow> BuildCanonicalLedgerHeaderQuery(
        int portfolioId, int leaseManagementId, int? restrictToTenantId = null) =>
        from account in _db.TenantAccounts.AsNoTracking()
        join relationship in _db.LeaseManagements.AsNoTracking()
            on new { account.PortfolioId, account.LeaseManagementId }
            equals new { relationship.PortfolioId, LeaseManagementId = relationship.Id }
        join property in _db.Properties.AsNoTracking()
            on new { relationship.PortfolioId, Id = relationship.PropertyId }
            equals new { property.PortfolioId, property.Id }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { relationship.PortfolioId, LeaseManagementId = relationship.Id }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        join balance in _db.TenantAccountBalanceProjections.AsNoTracking()
            on new { account.PortfolioId, TenantAccountId = account.Id }
            equals new { balance.PortfolioId, balance.TenantAccountId }
        where relationship.PortfolioId == portfolioId
            && relationship.Id == leaseManagementId
            && (restrictToTenantId == null || _db.LeaseManagementParties.Any(party =>
                party.PortfolioId == portfolioId
                && party.LeaseManagementId == relationship.Id
                && party.TenantId == restrictToTenantId.Value))
        select new CanonicalLedgerHeaderReadRow
        {
            LeaseManagementId = relationship.Id,
            TenantAccountId = account.Id,
            AccountNumber = account.AccountNumber,
            TenantName = lifecycle.CurrentPrimaryTenantName,
            PropertyId = property.Id,
            PropertyName = property.Name,
            TotalDebits = balance.TotalDebits,
            TotalCredits = balance.TotalCredits,
            ReceivableBalance = balance.ReceivableBalance,
            PastDueCount = balance.PastDueCount,
            TotalEntryCount = _db.TenantLedgerEntries.Count(entry =>
                entry.PortfolioId == portfolioId
                && entry.TenantAccountId == account.Id
                && entry.EntryType != TenantLedgerEntryType.OpeningBalance),
        };

    internal IQueryable<CanonicalLedgerEntryReadRow> BuildCanonicalLedgerEntriesQuery(
        int portfolioId, int tenantAccountId) =>
        _db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.TenantAccountId == tenantAccountId
                && entry.EntryType != TenantLedgerEntryType.OpeningBalance)
            .Select(entry => new CanonicalLedgerEntryReadRow
            {
                Id = entry.Id,
                TenantAccountId = entry.TenantAccountId,
                EntryType = entry.EntryType,
                Direction = entry.Direction,
                Amount = entry.Amount,
                EffectiveOn = entry.EffectiveOn,
                DueOn = entry.DueOn,
                Description = entry.Description,
                PaymentMethodSummary = entry.ProviderPaymentAttempt != null
                    ? entry.ProviderPaymentAttempt.PaymentMethodSummary
                    : null,
                LeaseAgreementBaseRent = entry.LeaseAgreement != null
                    ? entry.LeaseAgreement.BaseRentAmount
                    : null,
            });

    private static LedgerTransactionResponse ToLedgerResponse(
        CanonicalLedgerEntryReadRow entry, CanonicalLedgerHeaderReadRow header, string? type = null) => new()
    {
        Date = entry.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        Type = type ?? (entry.EntryType == TenantLedgerEntryType.PaymentReceipt ? "Payment"
            : entry.Direction == TenantLedgerDirection.Debit ? "Charge" : "Credit"),
        Id = entry.Id,
        Description = entry.Description,
        Amount = entry.Direction == TenantLedgerDirection.Debit ? -entry.Amount : entry.Amount,
        PropertyId = header.PropertyId,
        PropertyName = header.PropertyName,
        Counterparty = string.IsNullOrWhiteSpace(header.TenantName) ? "Tenant" : header.TenantName,
        Category = entry.EntryType.ToString(),
        Status = "Posted",
        SourceHref = $"/tenant-accounts/{entry.TenantAccountId}/entries?entryId={entry.Id}",
        IsProrated = entry.EntryType == TenantLedgerEntryType.RentCharge
            && entry.LeaseAgreementBaseRent.HasValue
            && entry.Amount != entry.LeaseAgreementBaseRent.Value,
        Explanation = LedgerExplanation.ForTenantLedgerEntry(
            entry.EntryType, entry.Direction, entry.Amount, entry.EffectiveOn, entry.DueOn,
            entry.PaymentMethodSummary, entry.Description),
    };

    internal sealed class CanonicalLedgerHeaderReadRow
    {
        public int LeaseManagementId { get; init; }
        public int TenantAccountId { get; init; }
        public string AccountNumber { get; init; } = string.Empty;
        public string? TenantName { get; init; }
        public int PropertyId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public decimal TotalDebits { get; init; }
        public decimal TotalCredits { get; init; }
        public decimal ReceivableBalance { get; init; }
        public int PastDueCount { get; init; }
        public int TotalEntryCount { get; init; }
    }

    internal sealed class CanonicalLedgerEntryReadRow
    {
        public long Id { get; init; }
        public int TenantAccountId { get; init; }
        public TenantLedgerEntryType EntryType { get; init; }
        public TenantLedgerDirection Direction { get; init; }
        public decimal Amount { get; init; }
        public DateOnly EffectiveOn { get; init; }
        public DateOnly? DueOn { get; init; }
        public string Description { get; init; } = string.Empty;
        public string? PaymentMethodSummary { get; init; }
        public decimal? LeaseAgreementBaseRent { get; init; }
    }

    public async Task<LeaseResponse?> CreateAsync(int portfolioId, CreateLeaseRequest request, CancellationToken ct = default)
    {
        var suppliedTenantIds = NormalizeTenantIds(request.TenantId, request.TenantIds);
        if (request.NewTenant is not null && suppliedTenantIds.Count > 0)
        {
            throw new DomainValidationException(
                "Choose existing tenants or create one new tenant, not both.");
        }

        // Lease creation is one aggregate write. A generated tenant id requires an early SaveChanges,
        // and the lease/audit/deposit/opening-balance/rent rows require later saves, but every one of
        // those flushes participates in this transaction. Any failure rolls the entire user action back.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        // Verify the referenced property, unit, and tenant all live in the caller's portfolio.
        if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId, ct))
        {
            return null;
        }

        if (!await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId, request.PropertyId, ct))
        {
            return null;
        }

        if (request.NewTenant is null
            && !await AreTenantsInPortfolioAsync(portfolioId, suppliedTenantIds, ct))
        {
            return null;
        }

        var startUtc = request.StartDate.ToUtc();
        var endUtc = request.EndDate.ToUtc();
        var leaseNumber = await ResolveLeaseNumberAsync(portfolioId, request.LeaseNumber, startUtc, ct);

        // Clean 400 for an inverted range before the DB CHECK constraint turns it into a raw 500.
        EnsureValidDateRange(startUtc, endUtc);
        EnsureOpeningBalanceRequestIsValid(
            request.RentTrackingStartMode,
            request.OpeningBalanceAmount,
            request.OpeningBalanceAsOfDate,
            request.OpeningBalanceNote);

        // A new lease created already-occupying its unit (Active/NoticeGiven) must not double-book a unit
        // that another lease already holds over an overlapping range. A Draft/Pending lease books nothing,
        // so it skips the check. Id 0 (unsaved) never matches an existing row.
        if (OccupiesUnit(request.Status))
        {
            await LockUnitForLeaseMutationAsync(request.UnitId, ct);
            await EnsureUnitAvailableForOccupyingLeaseAsync(portfolioId, request.UnitId, ct);
            if (suppliedTenantIds.Count > 0)
            {
                await EnsureTenantsAvailableForOccupyingLeaseAsync(portfolioId, 0, suppliedTenantIds, ct);
            }
            await EnsureNoOverlappingActiveLeaseAsync(portfolioId, request.UnitId, 0, startUtc, endUtc, ct);
        }

        var now = _timeProvider.UtcNow();
        Tenant? createdTenant = null;
        IReadOnlyList<int> tenantIds = suppliedTenantIds;
        if (request.NewTenant is not null)
        {
            createdTenant = new Tenant
            {
                PortfolioId = portfolioId,
                FirstName = request.NewTenant.FirstName.Trim(),
                LastName = request.NewTenant.LastName.Trim(),
                Email = string.IsNullOrWhiteSpace(request.NewTenant.Email) ? null : request.NewTenant.Email.Trim(),
                Phone = string.IsNullOrWhiteSpace(request.NewTenant.Phone) ? null : request.NewTenant.Phone.Trim(),
                EmergencyContact = string.IsNullOrWhiteSpace(request.NewTenant.EmergencyContact)
                    ? null
                    : request.NewTenant.EmergencyContact.Trim(),
                DateOfBirth = request.NewTenant.DateOfBirth.ToUtc(),
                Notes = string.IsNullOrWhiteSpace(request.NewTenant.Notes) ? null : request.NewTenant.Notes.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Tenants.Add(createdTenant);
            await _db.SaveChangesAsync(ct);
            tenantIds = [createdTenant.Id];
        }

        var rentTrackingStartDate = request.Status == LeaseStatus.Active
            ? ResolveRentTrackingStartDate(startUtc, request.RentTrackingStartMode, request.RentTrackingStartDate, now.Date)
            : null;
        var entity = new Lease
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            TenantId = tenantIds[0],
            LeaseNumber = leaseNumber,
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
        SetLeaseTenantMemberships(entity, tenantIds, now);

        _db.Leases.Add(entity);

        // A lease created Active immediately occupies its unit. (A non-Active new lease never frees a unit
        // here — that only happens when an existing Active lease exits.) Tracked unit is saved in the same
        // transaction below.
        if (OccupiesUnit(entity.Status))
        {
            await SyncUnitOccupancyAsync(portfolioId, entity.UnitId, entity.Id, entity.Status, ct);
        }

        await _db.SaveChangesAsync(ct);

        // The generic Created twin already holds a full snapshot; add the legal change reason to it.
        await _audit.LogAsync(portfolioId, EntityType, entity.Id, AuditLogOperation.Created,
            changeReason: $"Lease {entity.LeaseNumber} created (status {entity.Status})", ct: ct);

        if (createdTenant is not null)
        {
            await EnsureCreatedTenantPortalAsync(createdTenant.Id, portfolioId, ct);
        }

        // Build the response while the transaction is still open. A query/materialization failure must
        // roll the write back instead of returning an error after the aggregate has already committed.
        var response = await GetAsync(portfolioId, entity.Id, ct) ?? LeaseResponse.FromEntity(entity);
        if (ownsTransaction)
        {
            await transaction!.CommitAsync(ct);
            await TryBroadcastLeaseMutationAsync(
                portfolioId: portfolioId,
                lease: response,
                tenant: createdTenant,
                deletedLeaseId: null,
                ct: ct);
        }
        return response;
    }

    private async Task TryBroadcastLeaseMutationAsync(
        int portfolioId,
        LeaseResponse? lease,
        Tenant? tenant,
        int? deletedLeaseId,
        CancellationToken ct)
    {
        try
        {
            if (tenant is not null)
            {
                await _dataUpdate.BroadcastEntityUpdateAsync(
                    portfolioId,
                    "Tenant",
                    tenant.Id,
                    TenantResponse.FromEntity(tenant),
                    ct);
            }
            if (lease is not null)
            {
                await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, lease.Id, lease, ct);
            }
            if (deletedLeaseId.HasValue)
            {
                await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, deletedLeaseId.Value, ct);
            }
        }
        catch (Exception ex)
        {
            // Realtime is cache invalidation, not part of the committed aggregate. A dropped client or
            // cancellation after commit must never make the API pretend the database write failed.
            _logger.LogWarning(ex,
                "Failed to broadcast committed lease mutation for portfolio {PortfolioId}; clients will refresh on their next query.",
                portfolioId);
        }
    }

    private async Task EnsureCreatedTenantPortalAsync(int tenantId, int portfolioId, CancellationToken ct)
    {
        if (_tenantPortalProvisioning is null)
        {
            return;
        }

        // Portal provisioning writes Identity and UserAccount rows through this scoped DbContext.
        // Keep those SaveChanges calls inside the lease transaction as part of the same user action.
        var result = await _tenantPortalProvisioning.EnsurePortalAccountForTenantAsync(
            tenantId,
            portfolioId,
            ct);
        if (result.Status is PortalAccountStatus.Failed or PortalAccountStatus.TenantNotFound)
        {
            throw new DomainValidationException(
                result.Error ?? "Tenant portal access could not be provisioned.");
        }
    }

    public async Task<LeaseResponse?> UpdateAsync(int portfolioId, int id, UpdateLeaseRequest request, CancellationToken ct = default)
    {
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        // Eager-load the label navigations so the PATCH response carries TenantName/PropertyName/UnitNumber
        // (the grid binds these) instead of flashing "-" until the next list refetch.
        var entity = await _db.Leases
            .Include(l => l.Tenant)
            .Include(l => l.LeaseTenants)
                .ThenInclude(lt => lt.Tenant)
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
        var shouldResolveRentTrackingStart =
            request.RentTrackingStartMode.HasValue ||
            (prevStatus != LeaseStatus.Active && newStatus == LeaseStatus.Active);
        var rentTrackingMode = request.RentTrackingStartMode
            ?? (shouldResolveRentTrackingStart ? RentTrackingStartMode.ForwardOnly : (RentTrackingStartMode?)null);
        var hasTenantUpdate = request.TenantId.HasValue || request.TenantIds is { Count: > 0 };
        IReadOnlyList<int>? requestedTenantIds = null;
        if (hasTenantUpdate)
        {
            requestedTenantIds = NormalizeTenantIds(request.TenantId, request.TenantIds);
            if (!await AreTenantsInPortfolioAsync(portfolioId, requestedTenantIds, ct))
            {
                return null;
            }
        }

        // 1. State machine: a status move must be a legal lifecycle edge (no-op same→same allowed).
        EnsureTransitionAllowed(prevStatus, newStatus);

        // 2. Date range: clean 400 before the DB CHECK constraint would 500.
        EnsureValidDateRange(newStartUtc, newEndUtc);
        if (rentTrackingMode.HasValue)
        {
            EnsureOpeningBalanceRequestIsValid(
                rentTrackingMode.Value,
                request.OpeningBalanceAmount,
                request.OpeningBalanceAsOfDate,
                request.OpeningBalanceNote);
        }
        else if (HasOpeningBalanceRequest(
            request.OpeningBalanceAmount,
            request.OpeningBalanceAsOfDate,
            request.OpeningBalanceNote))
        {
            throw new DomainValidationException(
                "Opening balance fields can only be used when updating the rent tracking option.");
        }

        // 3. Double-booking: when this edit makes the lease occupy the unit, or changes the date range of
        //    a lease already occupying the unit, reject if another occupying lease holds an overlapping
        //    range. A rent-only edit on an already-occupying lease does not re-run the check, so legacy
        //    overlapping data is not blocked unless the user changes the occupancy dates/status.
        var dateRangeChanged = newStartUtc != entity.StartDate || newEndUtc != entity.EndDate;
        if (OccupiesUnit(newStatus))
        {
            await LockUnitForLeaseMutationAsync(entity.UnitId, ct);
        }
        if (OccupiesUnit(newStatus) && (!OccupiesUnit(prevStatus) || dateRangeChanged))
        {
            await EnsureNoOverlappingActiveLeaseAsync(portfolioId, entity.UnitId, entity.Id, newStartUtc, newEndUtc, ct);
        }

        if (OccupiesUnit(newStatus) && !OccupiesUnit(prevStatus))
        {
            await EnsureUnitAvailableForOccupyingLeaseAsync(portfolioId, entity.UnitId, ct);
        }

        if (OccupiesUnit(newStatus) && (!OccupiesUnit(prevStatus) || requestedTenantIds is not null))
        {
            await EnsureTenantsAvailableForOccupyingLeaseAsync(
                portfolioId,
                entity.Id,
                requestedTenantIds ?? CurrentTenantIds(entity),
                ct);
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
        if (requestedTenantIds is not null)
        {
            SetLeaseTenantMemberships(entity, requestedTenantIds, _timeProvider.UtcNow());
        }
        if (rentTrackingMode.HasValue)
        {
            entity.RentTrackingStartDate = newStatus == LeaseStatus.Active
                ? ResolveRentTrackingStartDate(entity.StartDate, rentTrackingMode.Value, request.RentTrackingStartDate, _timeProvider.UtcNow().Date)
                : null;
        }
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = _timeProvider.UtcNow();

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

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? LeaseResponse.FromEntity(entity, includeNavigations: true);
        if (ownsTransaction)
        {
            await transaction!.CommitAsync(ct);
            await TryBroadcastLeaseMutationAsync(
                portfolioId: portfolioId,
                lease: response,
                tenant: null,
                deletedLeaseId: null,
                ct: ct);
        }
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        var entity = await _db.Leases
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // Capture the full pre-termination snapshot before the soft-delete (the generic twin would
        // otherwise record only the DeletedAt change).
        var before = Snapshot(entity);

        entity.DeletedAt = _timeProvider.UtcNow();

        // Terminating (soft-deleting) a lease is a real exit: free the unit unless another Active lease
        // still holds it. The soft-deleted lease is excluded both by the explicit Id guard and by the
        // global soft-delete query filter, so it never counts itself as the occupant. Tracked unit saves
        // in the same transaction below.
        await LockUnitForLeaseMutationAsync(entity.UnitId, ct);
        await SyncUnitOccupancyAsync(portfolioId, entity.UnitId, entity.Id, LeaseStatus.Terminated, ct);

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(portfolioId, EntityType, id, AuditLogOperation.Deleted,
            oldValues: before, changeReason: $"Lease {entity.LeaseNumber} terminated", ct: ct);

        if (ownsTransaction)
        {
            await transaction!.CommitAsync(ct);
            await TryBroadcastLeaseMutationAsync(
                portfolioId: portfolioId,
                lease: null,
                tenant: null,
                deletedLeaseId: id,
                ct: ct);
        }
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

        var data = new LeaseAgreementRenderData
        {
            PropertyId = lease.PropertyId,
            AgreementNumber = lease.LeaseNumber,
            TermStartOn = DateOnly.FromDateTime(lease.StartDate),
            TermEndOn = DateOnly.FromDateTime(lease.EndDate),
            BaseRentAmount = lease.MonthlyRent,
            SecurityDepositObligation = lease.SecurityDeposit,
            LateFeeAmount = lease.LateFeeAmount,
            RentDueDay = lease.RentDueDay,
            LandlordName = landlordName,
            TenantName = tenantName,
            TenantEmail = lease.Tenant?.Email ?? string.Empty,
            PropertyName = property?.Name ?? string.Empty,
            PropertyAddress = propertyAddress,
            UnitNumber = lease.Unit?.UnitNumber,
            State = property?.State ?? string.Empty,
            YearBuilt = property?.YearBuilt,
        };

        var rendered = _agreementRenderer is null
            ? new LeaseAgreementRenderResult(_pdf.Generate(data), null, null, null)
            : await _agreementRenderer.RenderAsync(portfolioId, data, ct);
        var pdfBytes = rendered.PdfBytes;

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
            UploadedAt = _timeProvider.UtcNow(),
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

        await FreezeDocumentTemplateAsync(
            portfolioId,
            lease.Id,
            rendered.DocumentTemplateId,
            rendered.DocumentTemplateVersion,
            ct);

        return new LeaseDocumentResponse(
            stored.Id,
            lease.Id,
            stored.FileName,
            stored.FileSize,
            $"/api/v1/leases/{lease.Id}/document",
            stored.UploadedAt);
    }

    private Task FreezeDocumentTemplateAsync(
        int portfolioId,
        int leaseId,
        int? documentTemplateId,
        int? documentTemplateVersion,
        CancellationToken ct)
    {
        var now = _timeProvider.UtcNow();
        return _db.Leases
            .Where(l => l.Id == leaseId && l.PortfolioId == portfolioId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(l => l.DocumentTemplateId, documentTemplateId)
                .SetProperty(l => l.DocumentTemplateVersion, documentTemplateVersion)
                .SetProperty(l => l.UpdatedAt, now), ct);
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
