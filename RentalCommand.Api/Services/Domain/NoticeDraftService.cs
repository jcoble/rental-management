using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Review/edit façade over the PostgreSQL set-based tenant-notice draft command. Generation never
/// loads policies, templates, candidate ids, or relationship rows into application memory.
/// </summary>
public sealed class NoticeDraftService : INoticeDraftService
{
    private readonly RentalCommandDbContext _db;
    private readonly ITenantNoticeDraftSetStore _drafts;
    private readonly TimeProvider _timeProvider;

    public NoticeDraftService(
        RentalCommandDbContext db,
        ITenantNoticeDraftSetStore drafts,
        TimeProvider timeProvider)
    {
        _db = db;
        _drafts = drafts;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<NoticeDraftResponse>> ListAsync(
        int portfolioId,
        string? status,
        ListQuery page,
        CancellationToken ct = default)
    {
        var query = ResponseQuery(portfolioId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(draft => draft.Status == status);
        }

        return await query
            .OrderBy(draft => draft.Status == "Draft" ? 0 : 1)
            .ThenBy(draft => draft.TriggerDate)
            .ThenByDescending(draft => draft.CreatedAt)
            .ThenByDescending(draft => draft.Id)
            .Skip(page.NormalizedSkip)
            .Take(page.NormalizedTake)
            .TagWith("TSK-668 tenant notice list projection")
            .ToListAsync(ct);
    }

    public async Task<GenerateNoticeDraftsResponse> GenerateAsync(
        int portfolioId,
        GenerateNoticeDraftsRequest? request = null,
        CancellationToken ct = default)
    {
        var generated = await _drafts.GenerateManualAsync(
            portfolioId,
            request?.RecipientTenantId,
            request?.LeaseManagementId,
            request?.TenantAccountId,
            request?.TenantLedgerEntryId,
            request?.NoticeType,
            ct);

        return new GenerateNoticeDraftsResponse
        {
            CreatedCount = generated.FirstOrDefault()?.CreatedCount ?? 0,
            Drafts = generated.Select(Map).ToList(),
        };
    }

    public async Task<NoticeDraftResponse?> UpdateAsync(
        int portfolioId,
        int id,
        UpdateNoticeDraftRequest request,
        CancellationToken ct = default)
    {
        var draft = await BaseQuery(portfolioId).FirstOrDefaultAsync(row => row.Id == id, ct);
        if (draft == null || draft.Status != "Draft") return null;

        if (!string.IsNullOrWhiteSpace(request.Subject)) draft.Subject = request.Subject.Trim();
        if (!string.IsNullOrWhiteSpace(request.Body)) draft.Body = request.Body.Trim();
        draft.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);
        return Map(draft);
    }

    public async Task<NoticeDraftResponse?> DismissAsync(
        int portfolioId,
        int id,
        CancellationToken ct = default)
    {
        var draft = await BaseQuery(portfolioId).FirstOrDefaultAsync(row => row.Id == id, ct);
        if (draft == null || draft.Status != "Draft") return null;

        draft.Status = "Dismissed";
        draft.DismissedAt = _timeProvider.UtcNow();
        draft.UpdatedAt = draft.DismissedAt.Value;
        await _db.SaveChangesAsync(ct);
        return Map(draft);
    }

    private IQueryable<NoticeDraft> BaseQuery(int portfolioId) =>
        _db.NoticeDrafts
            .Include(draft => draft.RecipientLeaseManagementParty).ThenInclude(party => party!.Tenant)
            .Include(draft => draft.Property)
            .Include(draft => draft.LeaseManagement).ThenInclude(management => management!.Unit)
            .Where(draft => draft.PortfolioId == portfolioId);

    private IQueryable<NoticeDraftResponse> ResponseQuery(int portfolioId) =>
        _db.NoticeDrafts.AsNoTracking()
            .Where(draft => draft.PortfolioId == portfolioId)
            .Select(draft => new NoticeDraftResponse
            {
                Id = draft.Id,
                LeaseManagementId = draft.LeaseManagementId,
                TenantAccountId = draft.TenantAccountId,
                RecipientLeaseManagementPartyId = draft.RecipientLeaseManagementPartyId,
                LeaseAgreementId = draft.LeaseAgreementId,
                LeaseAddendumId = draft.LeaseAddendumId,
                TenantLedgerEntryId = draft.TenantLedgerEntryId,
                RecipientTenantId = draft.RecipientLeaseManagementParty!.TenantId,
                PropertyId = draft.PropertyId,
                TenantName = (draft.RecipientLeaseManagementParty.Tenant!.FirstName + " "
                    + draft.RecipientLeaseManagementParty.Tenant.LastName).Trim(),
                PropertyName = draft.Property!.Name,
                UnitNumber = draft.LeaseManagement!.Unit!.UnitNumber,
                NoticeType = draft.NoticeType,
                Status = draft.Status,
                Subject = draft.Subject,
                Body = draft.Body,
                Reason = draft.Reason,
                TriggerDate = draft.TriggerDate,
                ConversationId = draft.ConversationId,
                ApprovedChannels = draft.ApprovedChannels,
                CreatedAt = draft.CreatedAt,
                UpdatedAt = draft.UpdatedAt,
                ApprovedAt = draft.ApprovedAt,
                DismissedAt = draft.DismissedAt,
            });

    private static NoticeDraftResponse Map(GeneratedTenantNoticeDraft draft) => new()
    {
        Id = draft.DraftId,
        LeaseManagementId = draft.LeaseManagementId,
        TenantAccountId = draft.TenantAccountId,
        RecipientLeaseManagementPartyId = draft.RecipientLeaseManagementPartyId,
        LeaseAgreementId = draft.LeaseAgreementId,
        LeaseAddendumId = draft.LeaseAddendumId,
        TenantLedgerEntryId = draft.TenantLedgerEntryId,
        RecipientTenantId = draft.RecipientTenantId,
        PropertyId = draft.PropertyId,
        TenantName = draft.TenantName,
        PropertyName = draft.PropertyName,
        UnitNumber = draft.UnitNumber,
        NoticeType = draft.NoticeType,
        Status = draft.Status,
        Subject = draft.Subject,
        Body = draft.Body,
        Reason = draft.Reason,
        TriggerDate = draft.TriggerDate,
        ConversationId = draft.ConversationId,
        ApprovedChannels = draft.ApprovedChannels,
        CreatedAt = draft.CreatedAt,
        UpdatedAt = draft.UpdatedAt,
        ApprovedAt = draft.ApprovedAt,
        DismissedAt = draft.DismissedAt,
    };

    private static NoticeDraftResponse Map(NoticeDraft draft)
    {
        var tenant = draft.RecipientLeaseManagementParty?.Tenant;
        return new NoticeDraftResponse
        {
            Id = draft.Id,
            LeaseManagementId = draft.LeaseManagementId,
            TenantAccountId = draft.TenantAccountId,
            RecipientLeaseManagementPartyId = draft.RecipientLeaseManagementPartyId,
            LeaseAgreementId = draft.LeaseAgreementId,
            LeaseAddendumId = draft.LeaseAddendumId,
            TenantLedgerEntryId = draft.TenantLedgerEntryId,
            RecipientTenantId = tenant?.Id ?? 0,
            PropertyId = draft.PropertyId,
            TenantName = tenant == null ? string.Empty : $"{tenant.FirstName} {tenant.LastName}".Trim(),
            PropertyName = draft.Property?.Name,
            UnitNumber = draft.LeaseManagement?.Unit?.UnitNumber,
            NoticeType = draft.NoticeType,
            Status = draft.Status,
            Subject = draft.Subject,
            Body = draft.Body,
            Reason = draft.Reason,
            TriggerDate = draft.TriggerDate,
            ConversationId = draft.ConversationId,
            ApprovedChannels = draft.ApprovedChannels,
            CreatedAt = draft.CreatedAt,
            UpdatedAt = draft.UpdatedAt,
            ApprovedAt = draft.ApprovedAt,
            DismissedAt = draft.DismissedAt,
        };
    }
}
