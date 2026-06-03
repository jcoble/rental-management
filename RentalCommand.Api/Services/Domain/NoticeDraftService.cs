using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class NoticeDraftService : INoticeDraftService
{
    private readonly RentalCommandDbContext _db;
    private readonly IConversationService _conversations;

    public NoticeDraftService(RentalCommandDbContext db, IConversationService conversations)
    {
        _db = db;
        _conversations = conversations;
    }

    public async Task<IReadOnlyList<NoticeDraftResponse>> ListAsync(
        int portfolioId,
        string? status,
        CancellationToken ct = default)
    {
        var query = BaseQuery(portfolioId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(d => d.Status == status);
        }

        var drafts = await query
            .OrderBy(d => d.Status == "Draft" ? 0 : 1)
            .ThenBy(d => d.TriggerDate)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        return drafts.Select(Map).ToList();
    }

    public async Task<GenerateNoticeDraftsResponse> GenerateAsync(int portfolioId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var created = new List<NoticeDraft>();

        var leases = await _db.Leases
            .Include(l => l.Tenant)
            .Include(l => l.Property)
            .Include(l => l.Unit)
            .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active)
            .ToListAsync(ct);

        foreach (var lease in leases)
        {
            if (lease.Tenant == null) continue;

            var daysToEnd = (lease.EndDate.Date - today).Days;
            if (daysToEnd >= 0 && daysToEnd <= 75)
            {
                await AddIfMissingAsync(created, BuildRenewalDraft(portfolioId, lease, daysToEnd, now), ct);
            }

            if (daysToEnd >= 0 && daysToEnd <= 30)
            {
                await AddIfMissingAsync(created, BuildMoveOutDraft(portfolioId, lease, daysToEnd, now), ct);
            }
        }

        var latePayments = await _db.Payments
            .Include(p => p.Lease).ThenInclude(l => l!.Tenant)
            .Include(p => p.Lease).ThenInclude(l => l!.Property)
            .Include(p => p.Lease).ThenInclude(l => l!.Unit)
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.DueDate.Date < today &&
                (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Late || p.Status == PaymentStatus.Partial))
            .ToListAsync(ct);

        foreach (var payment in latePayments)
        {
            if (payment.Lease?.Tenant == null) continue;
            var daysLate = (today - payment.DueDate.Date).Days;
            await AddIfMissingAsync(created, BuildLateDraft(portfolioId, payment, daysLate, now), ct);
        }

        if (created.Count > 0)
        {
            _db.NoticeDrafts.AddRange(created);
            await _db.SaveChangesAsync(ct);
        }

        return new GenerateNoticeDraftsResponse
        {
            CreatedCount = created.Count,
            Drafts = created.Select(Map).ToList()
        };
    }

    public async Task<NoticeDraftResponse?> UpdateAsync(
        int portfolioId,
        int id,
        UpdateNoticeDraftRequest request,
        CancellationToken ct = default)
    {
        var draft = await BaseQuery(portfolioId).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (draft == null || draft.Status != "Draft") return null;

        if (!string.IsNullOrWhiteSpace(request.Subject)) draft.Subject = request.Subject.Trim();
        if (!string.IsNullOrWhiteSpace(request.Body)) draft.Body = request.Body.Trim();
        draft.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Map(draft);
    }

    public async Task<NoticeDraftResponse?> ApproveAsync(
        int portfolioId,
        int id,
        ApproveNoticeDraftRequest request,
        CancellationToken ct = default)
    {
        var draft = await BaseQuery(portfolioId).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (draft == null || draft.Status != "Draft") return null;

        var channels = NormalizeChannels(request.Channels);
        if (channels.Count == 0) channels = ["Portal"];

        var conversation = await _conversations.StartAsync(
            portfolioId,
            draft.TenantId,
            draft.Subject,
            draft.Body,
            channels,
            ct);
        if (conversation == null) return null;

        draft.Status = "Approved";
        draft.ApprovedAt = DateTime.UtcNow;
        draft.UpdatedAt = draft.ApprovedAt.Value;
        draft.ConversationId = conversation.Id;
        draft.ApprovedChannels = string.Join(",", channels);
        await _db.SaveChangesAsync(ct);

        return Map(draft);
    }

    public async Task<NoticeDraftResponse?> DismissAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var draft = await BaseQuery(portfolioId).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (draft == null || draft.Status != "Draft") return null;

        draft.Status = "Dismissed";
        draft.DismissedAt = DateTime.UtcNow;
        draft.UpdatedAt = draft.DismissedAt.Value;
        await _db.SaveChangesAsync(ct);
        return Map(draft);
    }

    private IQueryable<NoticeDraft> BaseQuery(int portfolioId) =>
        _db.NoticeDrafts
            .Include(d => d.Tenant)
            .Include(d => d.Property)
            .Include(d => d.Lease).ThenInclude(l => l!.Unit)
            .Where(d => d.PortfolioId == portfolioId);

    private async Task AddIfMissingAsync(List<NoticeDraft> created, NoticeDraft draft, CancellationToken ct)
    {
        var existsInDb = await _db.NoticeDrafts.AnyAsync(d =>
            d.PortfolioId == draft.PortfolioId &&
            d.LeaseId == draft.LeaseId &&
            d.NoticeType == draft.NoticeType &&
            d.Status == "Draft", ct);
        var existsInBatch = created.Any(d =>
            d.PortfolioId == draft.PortfolioId &&
            d.LeaseId == draft.LeaseId &&
            d.NoticeType == draft.NoticeType &&
            d.Status == "Draft");

        if (!existsInDb && !existsInBatch)
        {
            created.Add(draft);
        }
    }

    private static NoticeDraft BuildRenewalDraft(int portfolioId, Lease lease, int daysToEnd, DateTime now)
    {
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = string.IsNullOrWhiteSpace(lease.Unit?.UnitNumber) ? "" : $" Unit {lease.Unit.UnitNumber}";
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        return new NoticeDraft
        {
            PortfolioId = portfolioId,
            LeaseId = lease.Id,
            TenantId = lease.TenantId,
            PropertyId = lease.PropertyId,
            NoticeType = "RenewalOffer",
            Subject = $"Lease renewal for {propertyName}{unit}",
            Body = $"Hi {tenantName}, your current lease for {propertyName}{unit} ends on {lease.EndDate:MMMM d, yyyy}. "
                 + $"We would like to offer a renewal at {lease.MonthlyRent:C0} per month. "
                 + "Please reply here if you would like to renew or if you have questions.",
            Reason = $"Lease ends in {daysToEnd} days.",
            TriggerDate = lease.EndDate.Date,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static NoticeDraft BuildMoveOutDraft(int portfolioId, Lease lease, int daysToEnd, DateTime now)
    {
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = string.IsNullOrWhiteSpace(lease.Unit?.UnitNumber) ? "" : $" Unit {lease.Unit.UnitNumber}";
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        return new NoticeDraft
        {
            PortfolioId = portfolioId,
            LeaseId = lease.Id,
            TenantId = lease.TenantId,
            PropertyId = lease.PropertyId,
            NoticeType = "MoveOutReminder",
            Subject = $"Move-out reminder for {propertyName}{unit}",
            Body = $"Hi {tenantName}, this is a reminder that your lease for {propertyName}{unit} ends on {lease.EndDate:MMMM d, yyyy}. "
                 + "Please reply to coordinate keys, inspection timing, and forwarding-address details.",
            Reason = $"Lease ends in {daysToEnd} days.",
            TriggerDate = lease.EndDate.Date,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static NoticeDraft BuildLateDraft(int portfolioId, Payment payment, int daysLate, DateTime now)
    {
        var lease = payment.Lease!;
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = string.IsNullOrWhiteSpace(lease.Unit?.UnitNumber) ? "" : $" Unit {lease.Unit.UnitNumber}";
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        return new NoticeDraft
        {
            PortfolioId = portfolioId,
            LeaseId = lease.Id,
            TenantId = lease.TenantId,
            PropertyId = lease.PropertyId,
            NoticeType = "LateRentNotice",
            Subject = $"Past-due rent for {propertyName}{unit}",
            Body = $"Hi {tenantName}, our records show {payment.Amount:C0} due on {payment.DueDate:MMMM d, yyyy} "
                 + $"for {propertyName}{unit}. Please reply with payment status or questions.",
            Reason = $"Payment is {daysLate} days past due.",
            TriggerDate = payment.DueDate.Date,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static List<string> NormalizeChannels(List<string>? channels) =>
        (channels ?? [])
            .Select(c => c.Trim().ToLowerInvariant() switch
            {
                "portal" => "Portal",
                "email" => "Email",
                "sms" => "Sms",
                _ => ""
            })
            .Where(c => c.Length > 0)
            .Distinct()
            .ToList();

    private static NoticeDraftResponse Map(NoticeDraft d)
    {
        var tenantName = d.Tenant == null ? "" : $"{d.Tenant.FirstName} {d.Tenant.LastName}".Trim();
        return new NoticeDraftResponse
        {
            Id = d.Id,
            LeaseId = d.LeaseId,
            TenantId = d.TenantId,
            PropertyId = d.PropertyId,
            TenantName = tenantName,
            PropertyName = d.Property?.Name,
            UnitNumber = d.Lease?.Unit?.UnitNumber,
            NoticeType = d.NoticeType,
            Status = d.Status,
            Subject = d.Subject,
            Body = d.Body,
            Reason = d.Reason,
            TriggerDate = d.TriggerDate,
            ConversationId = d.ConversationId,
            ApprovedChannels = d.ApprovedChannels,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
            ApprovedAt = d.ApprovedAt,
            DismissedAt = d.DismissedAt
        };
    }
}
