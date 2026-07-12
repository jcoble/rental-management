using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPortalService"/>
public class PortalService : IPortalService
{
    private readonly RentalCommandDbContext _db;
    private readonly ILeaseQaService _leaseQa;
    private readonly TimeProvider _timeProvider;

    public PortalService(RentalCommandDbContext db, ILeaseQaService leaseQa, TimeProvider timeProvider)
    {
        _db = db;
        _leaseQa = leaseQa;
        _timeProvider = timeProvider;
    }

    public Task<int?> ResolveTenantIdAsync(
        int portfolioId,
        int accessContextId,
        CancellationToken ct = default) =>
        _db.EffectiveTenantAccess
            .AsNoTracking()
            .Where(access => access.PortfolioId == portfolioId &&
                access.AccessContextId == accessContextId)
            .OrderBy(access => access.LeaseManagementPartyId)
            .Select(access => (int?)access.TenantId)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<LeaseResponse>> GetLeasesAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        return await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.TenantId == tenantId)
            .OrderByDescending(l => l.StartDate)
            .Select(l => new LeaseResponse
            {
                Id = l.Id,
                PortfolioId = l.PortfolioId,
                PropertyId = l.PropertyId,
                UnitId = l.UnitId,
                TenantId = l.TenantId,
                LeaseNumber = l.LeaseNumber,
                Status = l.Status,
                StartDate = l.StartDate,
                EndDate = l.EndDate,
                MoveInDate = l.MoveInDate,
                MoveOutDate = l.MoveOutDate,
                MonthlyRent = l.MonthlyRent,
                SecurityDeposit = l.SecurityDeposit,
                LateFeeAmount = l.LateFeeAmount,
                RentDueDay = l.RentDueDay,
                Notes = l.Notes,
                CreatedAt = l.CreatedAt,
                UpdatedAt = l.UpdatedAt,
                TenantName = l.Tenant == null ? null : (l.Tenant.FirstName + " " + l.Tenant.LastName).Trim(),
                UnitNumber = l.Unit == null ? null : l.Unit.UnitNumber,
                PropertyName = l.Property == null ? null : l.Property.Name,
            })
            .ToListAsync(ct);
    }

    public async Task<PortalBalanceResponse> GetBalanceAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var financialRelationships = _db.LeaseManagementParties
            .AsNoTracking()
            .Where(party => party.PortfolioId == portfolioId
                && party.TenantId == tenantId
                && (party.Role == LeaseManagementPartyRole.PrimaryTenant
                    || party.Role == LeaseManagementPartyRole.CoTenant
                    || party.Role == LeaseManagementPartyRole.Guarantor));

        // One SQL statement authorizes every account through a party row belonging to this tenant.
        // Receivable and past-due facts come from the database projections; collected cash is the net
        // value of immutable receipt credits after any immutable reversals.
        var rollup = await _db.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.PortfolioId == portfolioId && tenant.Id == tenantId)
            .Select(_ => new
            {
                Collected = _db.TenantLedgerEntries
                    .Where(entry => entry.PortfolioId == portfolioId
                        && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                        && financialRelationships.Any(party =>
                            party.LeaseManagementId == entry.TenantAccount!.LeaseManagementId))
                    .Sum(entry => (decimal?)(entry.Amount -
                        (_db.TenantLedgerEntries
                            .Where(reversal => reversal.PortfolioId == portfolioId
                                && reversal.TenantAccountId == entry.TenantAccountId
                                && reversal.EntryType == TenantLedgerEntryType.Reversal
                                && reversal.ReversesEntryId == entry.Id)
                            .Sum(reversal => (decimal?)reversal.Amount) ?? 0m))) ?? 0m,
                Outstanding = _db.TenantAccountBalanceProjections
                    .Where(balance => balance.PortfolioId == portfolioId
                        && financialRelationships.Any(party =>
                            party.LeaseManagementId == balance.LeaseManagementId))
                    .Sum(balance => (decimal?)balance.ReceivableBalance) ?? 0m,
                Overdue = _db.TenantAccountBalanceProjections
                    .Where(balance => balance.PortfolioId == portfolioId
                        && financialRelationships.Any(party =>
                            party.LeaseManagementId == balance.LeaseManagementId))
                    .Sum(balance => (decimal?)balance.PastDueAmount) ?? 0m,
                OverdueCount = _db.TenantAccountBalanceProjections
                    .Where(balance => balance.PortfolioId == portfolioId
                        && financialRelationships.Any(party =>
                            party.LeaseManagementId == balance.LeaseManagementId))
                    .Sum(balance => (int?)balance.PastDueCount) ?? 0,
            })
            .SingleOrDefaultAsync(ct);

        return new PortalBalanceResponse
        {
            TenantId = tenantId,
            Collected = rollup?.Collected ?? 0m,
            Outstanding = rollup?.Outstanding ?? 0m,
            Overdue = rollup?.Overdue ?? 0m,
            OverdueCount = rollup?.OverdueCount ?? 0,
        };
    }

    public async Task<IReadOnlyList<PortalPaymentResponse>> GetPaymentsAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var financialRelationships = _db.LeaseManagementParties
            .AsNoTracking()
            .Where(party => party.PortfolioId == portfolioId
                && party.TenantId == tenantId
                && (party.Role == LeaseManagementPartyRole.PrimaryTenant
                    || party.Role == LeaseManagementPartyRole.CoTenant
                    || party.Role == LeaseManagementPartyRole.Guarantor));

        var payments = await (
            from charge in _db.TenantChargeBalanceProjections.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { charge.PortfolioId, charge.TenantAccountId }
                equals new { account.PortfolioId, TenantAccountId = account.Id }
            where charge.PortfolioId == portfolioId
                && financialRelationships.Any(party =>
                    party.LeaseManagementId == account.LeaseManagementId)
            orderby (charge.DueOn ?? charge.EffectiveOn) descending, charge.TenantLedgerEntryId descending
            select new PortalPaymentResponse
            {
                Id = charge.TenantLedgerEntryId,
                TenantAccountId = account.Id,
                LeaseManagementId = account.LeaseManagementId,
                PaymentType = charge.EntryType == "RentCharge" ? "Rent"
                    : charge.EntryType == "DepositCharge" ? "SecurityDeposit"
                    : charge.EntryType == "LateFeeCharge" ? "LateFee"
                    : "Other",
                Status = charge.OriginalAmount - charge.ReversedAmount <= 0m ? "Waived"
                    : charge.OpenAmount <= 0m ? "Paid"
                    : charge.NetAllocations > 0m ? "Partial"
                    : charge.IsPastDue ? "Late"
                    : "Scheduled",
                Amount = charge.OriginalAmount - charge.ReversedAmount,
                DueDate = (charge.DueOn ?? charge.EffectiveOn)
                    .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                PaidDate = charge.OpenAmount <= 0m
                    ? _db.TenantLedgerAllocations
                        .Where(allocation => allocation.PortfolioId == portfolioId
                            && allocation.TenantAccountId == account.Id
                            && allocation.DebitEntryId == charge.TenantLedgerEntryId
                            && allocation.CreditEntry!.EntryType == TenantLedgerEntryType.PaymentReceipt)
                        .Max(allocation => (DateTime?)allocation.CreditEntry!.PostedAtUtc)
                    : null,
                Method = _db.TenantLedgerAllocations
                    .Where(allocation => allocation.PortfolioId == portfolioId
                        && allocation.TenantAccountId == account.Id
                        && allocation.DebitEntryId == charge.TenantLedgerEntryId
                        && allocation.CreditEntry!.EntryType == TenantLedgerEntryType.PaymentReceipt)
                    .OrderByDescending(allocation => allocation.AllocatedAtUtc)
                    .ThenByDescending(allocation => allocation.Id)
                    .Select(allocation => allocation.CreditEntry!.ProviderPaymentAttempt == null
                        ? null
                        : allocation.CreditEntry.ProviderPaymentAttempt.PaymentMethodSummary)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return payments;
    }

    public async Task<IReadOnlyList<AppointmentResponse>> GetAppointmentsAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();

        return await _db.Appointments
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId
                && a.TenantId == tenantId
                && a.ScheduledStart >= now
                && (a.Status == AppointmentStatus.Scheduled || a.Status == AppointmentStatus.Confirmed))
            .OrderBy(a => a.ScheduledStart)
            .ThenBy(a => a.Id)
            .Take(50)
            .Select(a => new AppointmentResponse
            {
                Id = a.Id,
                PortfolioId = a.PortfolioId,
                PropertyId = a.PropertyId,
                UnitId = a.UnitId,
                LeaseId = a.LeaseId,
                TenantId = a.TenantId,
                Title = a.Title,
                ProspectName = a.ProspectName,
                ProspectEmail = a.ProspectEmail,
                Type = a.Type,
                Status = a.Status,
                ScheduledStart = a.ScheduledStart,
                ScheduledEnd = a.ScheduledEnd,
                AssignedTo = a.AssignedTo,
                Notes = a.Notes,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt,
                PropertyName = a.Property == null ? null : a.Property.Name,
                UnitNumber = a.Unit == null
                    ? _db.Leases
                        .Where(l => l.PortfolioId == portfolioId && l.TenantId == tenantId)
                        .OrderByDescending(l => l.Status == LeaseStatus.Active)
                        .ThenByDescending(l => l.EndDate)
                        .ThenByDescending(l => l.Id)
                        .Select(l => l.Unit == null ? null : l.Unit.UnitNumber)
                        .FirstOrDefault()
                    : a.Unit.UnitNumber,
                TenantName = a.Tenant == null ? null : (a.Tenant.FirstName + " " + a.Tenant.LastName).Trim(),
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkOrderResponse>> GetWorkOrdersAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var workOrders = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId && w.TenantId == tenantId)
            .OrderByDescending(w => w.RequestedAt)
            .ToListAsync(ct);

        return workOrders.Select(WorkOrderResponse.FromEntity).ToList();
    }

    public async Task<WorkOrderDetailResponse?> GetWorkOrderDetailAsync(
        int portfolioId, int tenantId, int workOrderId, CancellationToken ct = default)
    {
        // Ownership is part of the lookup: a work order on another tenant's lease/unit simply isn't
        // found, so we never leak its existence or its timeline. Mirrors the lease-ledger restriction.
        var workOrder = await _db.WorkOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(
                w => w.Id == workOrderId && w.PortfolioId == portfolioId && w.TenantId == tenantId, ct);
        if (workOrder is null)
        {
            return null;
        }

        var events = await _db.WorkOrderStatusEvents
            .AsNoTracking()
            .Where(e => e.WorkOrderId == workOrderId && e.PortfolioId == portfolioId)
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        return WorkOrderDetailResponse.FromEntity(workOrder, events);
    }

    public async Task<WorkOrderResponse?> CreateTenantWorkOrderAsync(
        int portfolioId,
        int tenantId,
        CreateTenantWorkOrderRequest request,
        CancellationToken ct = default)
    {
        var lease = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.TenantId == tenantId)
            .OrderByDescending(l => l.Status == LeaseStatus.Active)
            .ThenByDescending(l => l.EndDate)
            .Select(l => new { l.Id, l.PropertyId, l.UnitId })
            .FirstOrDefaultAsync(ct);

        if (lease is null)
        {
            return null;
        }

        var now = _timeProvider.UtcNow();
        var workOrder = new WorkOrder
        {
            PortfolioId = portfolioId,
            PropertyId = lease.PropertyId,
            UnitId = lease.UnitId,
            TenantId = tenantId,
            LeaseId = lease.Id,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? "Resident Request" : request.Category.Trim(),
            Priority = request.Priority,
            Status = WorkOrderStatus.New,
            RequestedAt = now,
            CreatedBy = "Tenant",
            UpdatedAt = now,
        };

        // Seed the timeline with the initial null → New event so the tenant's own request shows a
        // status stream from the moment they submit it.
        workOrder.StatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = portfolioId,
            FromStatus = null,
            ToStatus = workOrder.Status,
            Note = null,
            ChangedByUserId = null,
            ChangedByLabel = "Tenant",
            CreatedAtUtc = now,
        });

        _db.WorkOrders.Add(workOrder);
        await _db.SaveChangesAsync(ct);

        return WorkOrderResponse.FromEntity(workOrder);
    }

    public async Task<LeaseQuestionResponse?> AskLeaseAsync(
        int portfolioId, int tenantId, int? leaseId, string question, CancellationToken ct = default)
    {
        // Resolve to one of THIS tenant's own leases. An explicit leaseId is ownership-checked; a
        // null one falls back to the tenant's most relevant lease. A lease that isn't the tenant's
        // (or no lease at all) yields null → the controller maps that to 404, so a tenant can never
        // ask about another tenant's lease. IDOR gate.
        var resolvedLeaseId = await ResolveOwnedLeaseIdAsync(portfolioId, tenantId, leaseId, ct);
        if (resolvedLeaseId == null)
        {
            return null;
        }

        // The Q&A service is portfolio-scoped; ownership has already been enforced above.
        return await _leaseQa.AskAsync(portfolioId, resolvedLeaseId.Value, question, ct);
    }

    public async Task<AutopayStatusResponse?> GetAutopayStatusAsync(
        int portfolioId, int tenantId, int tenantAccountId, CancellationToken ct = default)
    {
        var businessDate = DateOnly.FromDateTime(_timeProvider.UtcNow());
        return await OwnedAutopayQuery(portfolioId, tenantId, tenantAccountId, businessDate)
            .AsNoTracking()
            .Select(row => new AutopayStatusResponse
            {
                TenantAccountId = row.Account.Id,
                Active = row.Enrollment != null,
                EnrolledAt = row.Enrollment == null ? null : row.Enrollment.EnrolledAtUtc,
                OnlinePaymentsAvailable = true,
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AutopayStatusResponse?> CancelAutopayAsync(
        int portfolioId, int tenantId, int tenantAccountId, CancellationToken ct = default)
    {
        var businessDate = DateOnly.FromDateTime(_timeProvider.UtcNow());
        var target = await OwnedAutopayQuery(portfolioId, tenantId, tenantAccountId, businessDate)
            .FirstOrDefaultAsync(ct);
        if (target == null) return null;
        if (target.Enrollment != null)
        {
            target.Enrollment.CanceledAtUtc = _timeProvider.UtcNow();
            target.Enrollment.CancelReason = "Canceled by tenant";
            await _db.SaveChangesAsync(ct);
        }

        return new AutopayStatusResponse
        {
            TenantAccountId = tenantAccountId,
            Active = false,
            OnlinePaymentsAvailable = true,
        };
    }

    private IQueryable<OwnedAutopayRow> OwnedAutopayQuery(
        int portfolioId, int tenantId, int tenantAccountId, DateOnly businessDate) =>
        from account in _db.TenantAccounts
        join party in _db.LeaseManagementParties
            on new { account.LeaseManagementId, account.PortfolioId }
            equals new { party.LeaseManagementId, party.PortfolioId }
        join access in _db.TenantUserAccesses
            on new { LeaseManagementPartyId = party.Id, party.PortfolioId }
            equals new { access.LeaseManagementPartyId, access.PortfolioId }
        from enrollment in _db.TenantAutopayEnrollments
            .Where(item => item.TenantAccountId == account.Id
                && item.PortfolioId == account.PortfolioId
                && item.CanceledAtUtc == null)
            .DefaultIfEmpty()
        where account.Id == tenantAccountId
            && account.PortfolioId == portfolioId
            && account.ClosedAtUtc == null
            && party.TenantId == tenantId
            && party.EffectiveFrom <= businessDate
            && (party.EffectiveThrough == null || party.EffectiveThrough >= businessDate)
            && party.Role != LeaseManagementPartyRole.Occupant
            && access.RevokedAtUtc == null
        orderby party.Id
        select new OwnedAutopayRow(account, enrollment);

    private sealed record OwnedAutopayRow(TenantAccount Account, TenantAutopayEnrollment? Enrollment);

    /// <summary>
    /// Validates that <paramref name="leaseId"/> (if given) is the tenant's own lease; when null,
    /// returns the tenant's most relevant lease id (active-preferred). Returns null when the tenant
    /// has no matching lease — the ownership/IDOR gate for the autopay endpoints.
    /// </summary>
    private async Task<int?> ResolveOwnedLeaseIdAsync(int portfolioId, int tenantId, int? leaseId, CancellationToken ct)
    {
        if (leaseId.HasValue)
        {
            var owns = await _db.Leases.AnyAsync(
                l => l.Id == leaseId.Value && l.PortfolioId == portfolioId && l.TenantId == tenantId, ct);
            return owns ? leaseId.Value : (int?)null;
        }

        var resolved = await _db.Leases
            .Where(l => l.PortfolioId == portfolioId && l.TenantId == tenantId)
            .OrderByDescending(l => l.Status == LeaseStatus.Active)
            .ThenByDescending(l => l.EndDate)
            .Select(l => (int?)l.Id)
            .FirstOrDefaultAsync(ct);

        return resolved;
    }
}
