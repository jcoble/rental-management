using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPortalService"/>
public class PortalService : IPortalService
{
    private readonly RentalCommandDbContext _db;

    public PortalService(RentalCommandDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LeaseResponse>> GetLeasesAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var leases = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.TenantId == tenantId)
            .OrderByDescending(l => l.StartDate)
            .ToListAsync(ct);

        return leases.Select(l => LeaseResponse.FromEntity(l)).ToList();
    }

    public async Task<PortalBalanceResponse> GetBalanceAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var payments = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                _db.Leases.Any(l => l.Id == p.LeaseId && l.TenantId == tenantId))
            .Select(p => new { p.Status, p.Amount, p.DueDate })
            .ToListAsync(ct);

        var result = new PortalBalanceResponse { TenantId = tenantId };
        foreach (var p in payments)
        {
            if (p.Status == PaymentStatus.Paid)
            {
                result.Collected += p.Amount;
                continue;
            }

            // Waived/Failed/Refunded are not money currently owed.
            var owed = p.Status is PaymentStatus.Scheduled or PaymentStatus.Partial or PaymentStatus.Late;
            if (!owed)
            {
                continue;
            }

            result.Outstanding += p.Amount;
            if (p.Status == PaymentStatus.Late || p.DueDate < now)
            {
                result.Overdue += p.Amount;
                result.OverdueCount++;
            }
        }

        return result;
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
                LeaseId = p.LeaseId,
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

        var now = DateTime.UtcNow;
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
            enrollment.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return new AutopayStatusResponse { LeaseId = leaseId, Active = false };
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
