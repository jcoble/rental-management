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
        var overdueCutoffUtc = _timeProvider.UtcNow().Date;

        // All four figures are conditional SUM/COUNT aggregates computed SQL-side in a single grouped
        // round-trip — no payment rows are pulled into memory. Partial-aware: a Paid payment contributes
        // its full Amount to Collected; a Partial contributes its collected AmountPaid to Collected and
        // only its unpaid remainder (Amount − AmountPaid) to Outstanding/Overdue; Scheduled/Late/Failed
        // owe in full — a Failed charge collected nothing, so the whole Amount is still owed (matching the
        // payments UI, which marks Failed as "still owed" and keeps it payable). Due-today payments are
        // outstanding, not overdue; overdue starts at the next UTC calendar day boundary. Waived/Refunded
        // are not money currently owed.
        var rollup = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                _db.Leases.Any(l => l.Id == p.LeaseId && l.TenantId == tenantId))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Collected = g.Sum(p =>
                    p.Status == PaymentStatus.Paid ? p.Amount
                    : p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m)
                    : 0m),
                Outstanding = g.Sum(p =>
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Late || p.Status == PaymentStatus.Failed) ? p.Amount
                    : p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m)
                    : 0m),
                Overdue = g.Sum(p =>
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial || p.Status == PaymentStatus.Late || p.Status == PaymentStatus.Failed)
                    && (p.Status == PaymentStatus.Late || p.DueDate < overdueCutoffUtc)
                        ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount)
                        : 0m),
                OverdueCount = g.Count(p =>
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial || p.Status == PaymentStatus.Late || p.Status == PaymentStatus.Failed)
                    && (p.Status == PaymentStatus.Late || p.DueDate < overdueCutoffUtc)),
            })
            .FirstOrDefaultAsync(ct);

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
        var payments = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                _db.Leases.Any(l => l.Id == p.LeaseId && l.TenantId == tenantId))
            .OrderByDescending(p => p.DueDate)
            .Select(p => new PortalPaymentResponse
            {
                Id = p.Id,
                // Portal payments are scoped to the tenant's lease (filtered above), so never lease-less.
                LeaseId = p.LeaseId!.Value,
                PaymentType = p.PaymentType.ToString(),
                Status = p.Status.ToString(),
                Amount = p.Amount,
                DueDate = p.DueDate,
                PaidDate = p.PaidDate,
                Method = p.Method,
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
        int portfolioId, int tenantId, int? leaseId, CancellationToken ct = default)
    {
        // Resolve which lease we're reporting on. An explicit leaseId is ownership-checked; a null
        // one falls back to the tenant's most relevant lease (active-preferred, latest end).
        var resolvedLeaseId = await ResolveOwnedLeaseIdAsync(portfolioId, tenantId, leaseId, ct);
        if (resolvedLeaseId == null)
        {
            return null;
        }

        var enrollment = await _db.AutopayEnrollments
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.LeaseId == resolvedLeaseId.Value && e.Active, ct);

        return new AutopayStatusResponse
        {
            LeaseId = resolvedLeaseId.Value,
            Active = enrollment != null,
            EnrolledAt = enrollment?.CreatedAt,
            OnlinePaymentsAvailable = true,
        };
    }

    public async Task<AutopayStatusResponse?> CancelAutopayAsync(
        int portfolioId, int tenantId, int leaseId, CancellationToken ct = default)
    {
        // Ownership: the lease must belong to this tenant or there is nothing to cancel (→ 404).
        var owns = await _db.Leases
            .AnyAsync(l => l.Id == leaseId && l.PortfolioId == portfolioId && l.TenantId == tenantId, ct);
        if (!owns)
        {
            return null;
        }

        var enrollment = await _db.AutopayEnrollments
            .FirstOrDefaultAsync(e => e.LeaseId == leaseId && e.Active, ct);
        if (enrollment != null)
        {
            enrollment.Active = false;
            enrollment.UpdatedAt = _timeProvider.UtcNow();
            await _db.SaveChangesAsync(ct);
        }

        return new AutopayStatusResponse { LeaseId = leaseId, Active = false, OnlinePaymentsAvailable = true };
    }

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
