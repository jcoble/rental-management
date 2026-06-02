using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
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
}
