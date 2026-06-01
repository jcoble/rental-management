using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPortalService"/>
public class PortalService : IPortalService
{
    private const string EntityType = "PortalMessage";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public PortalService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<LeaseResponse>> GetLeasesAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var leases = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.TenantId == tenantId)
            .OrderByDescending(l => l.StartDate)
            .ToListAsync(ct);

        return leases.Select(LeaseResponse.FromEntity).ToList();
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

    public async Task<IReadOnlyList<PortalMessageResponse>> GetMessagesAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        // PortalMessage has no TenantId column; scope a tenant's messages to the properties they hold a
        // lease on within this portfolio (plus any portfolio-wide messages with no property attached).
        var propertyIds = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.TenantId == tenantId)
            .Select(l => l.PropertyId)
            .Distinct()
            .ToListAsync(ct);

        var messages = await _db.PortalMessages
            .AsNoTracking()
            .Where(m => m.PortfolioId == portfolioId &&
                (m.PropertyId == null || propertyIds.Contains(m.PropertyId.Value)))
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new PortalMessageResponse
            {
                Id = m.Id,
                PortfolioId = m.PortfolioId,
                UserAccountId = m.UserAccountId,
                PropertyId = m.PropertyId,
                UnitId = m.UnitId,
                Subject = m.Subject,
                Body = m.Body,
                Status = m.Status.ToString(),
                Reply = m.Reply,
                CreatedAt = m.CreatedAt,
                UpdatedAt = m.UpdatedAt,
            })
            .ToListAsync(ct);

        return messages;
    }

    public async Task<PortalMessageResponse?> CreateMessageAsync(
        int portfolioId, int tenantId, CreatePortalMessageRequest request, CancellationToken ct = default)
    {
        // Resolve the UserAccount for this tenant in the portfolio.
        var userAccountId = await _db.UserAccounts
            .Where(ua => ua.PortfolioId == portfolioId && ua.TenantId == tenantId)
            .Select(ua => (int?)ua.Id)
            .FirstOrDefaultAsync(ct);

        if (userAccountId == null)
        {
            // No portal account for this tenant — cannot attribute the message.
            return null;
        }

        // When PropertyId is supplied verify it belongs to this portfolio.
        if (request.PropertyId.HasValue &&
            !await _db.Properties.AnyAsync(
                p => p.Id == request.PropertyId.Value && p.PortfolioId == portfolioId && p.DeletedAt == null, ct))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new PortalMessage
        {
            PortfolioId = portfolioId,
            UserAccountId = userAccountId.Value,
            PropertyId = request.PropertyId,
            Subject = request.Subject,
            Body = request.Body,
            Status = PortalMessageStatus.Open,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.PortalMessages.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = MapToPortalMessageResponse(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<PortalMessageResponse?> UpdateMessageStatusAsync(
        int portfolioId, int tenantId, int id, string? status, CancellationToken ct = default)
    {
        // Resolve the set of property ids the tenant holds a lease on (same scope logic as GetMessagesAsync).
        var propertyIds = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.TenantId == tenantId)
            .Select(l => l.PropertyId)
            .Distinct()
            .ToListAsync(ct);

        var entity = await _db.PortalMessages
            .FirstOrDefaultAsync(m =>
                m.Id == id &&
                m.PortfolioId == portfolioId &&
                (m.PropertyId == null || propertyIds.Contains(m.PropertyId.Value)), ct);

        if (entity == null)
        {
            return null;
        }

        // Tenants may only set Open or Closed; anything else is silently ignored.
        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<PortalMessageStatus>(status, ignoreCase: true, out var parsed) &&
            (parsed == PortalMessageStatus.Open || parsed == PortalMessageStatus.Closed))
        {
            entity.Status = parsed;
            entity.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return MapToPortalMessageResponse(entity);
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    private static PortalMessageResponse MapToPortalMessageResponse(PortalMessage m) => new()
    {
        Id = m.Id,
        PortfolioId = m.PortfolioId,
        UserAccountId = m.UserAccountId,
        PropertyId = m.PropertyId,
        UnitId = m.UnitId,
        Subject = m.Subject,
        Body = m.Body,
        Status = m.Status.ToString(),
        Reply = m.Reply,
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
    };
}
