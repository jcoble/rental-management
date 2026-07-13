using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
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
        WorkspaceReadScope scope,
        string? status,
        ListQuery page,
        CancellationToken ct = default)
    {
        var query = ResponseQuery(scope, _timeProvider.UtcNow());
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

    public Task<NoticeDraftResponse?> GetAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default) =>
        ResponseQuery(scope, _timeProvider.UtcNow())
            .Where(draft => draft.Id == id)
            .TagWith("TSK-668 authorized tenant notice detail")
            .SingleOrDefaultAsync(ct);

    public async Task<GenerateNoticeDraftsResponse> GenerateAsync(
        WorkspaceReadScope scope,
        GenerateNoticeDraftsRequest? request = null,
        CancellationToken ct = default)
    {
        return await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var securityNowUtc = _timeProvider.UtcNow();
            if (HasRelationshipFilter(request) &&
                !await ResolvesToAuthorizedPropertyAsync(scope, request!, securityNowUtc, innerCt))
            {
                return new GenerateNoticeDraftsResponse();
            }

            var generated = await _drafts.GenerateManualAsync(
                scope,
                request?.RecipientTenantId,
                request?.LeaseManagementId,
                request?.TenantAccountId,
                request?.TenantLedgerEntryId,
                request?.NoticeType,
                securityNowUtc,
                innerCt);

            return new GenerateNoticeDraftsResponse
            {
                CreatedCount = generated.FirstOrDefault()?.CreatedCount ?? 0,
                Drafts = generated.Select(Map).ToList(),
            };
        }, ct);
    }

    public async Task<NoticeDraftResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateNoticeDraftRequest request,
        CancellationToken ct = default)
    {
        return await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var draft = await BaseQuery(scope, _timeProvider.UtcNow())
                .FirstOrDefaultAsync(row => row.Id == id, innerCt);
            if (draft == null || draft.Status != "Draft") return null;

            if (!string.IsNullOrWhiteSpace(request.Subject)) draft.Subject = request.Subject.Trim();
            if (!string.IsNullOrWhiteSpace(request.Body)) draft.Body = request.Body.Trim();
            draft.UpdatedAt = _timeProvider.UtcNow();
            await _db.SaveChangesAsync(innerCt);
            return Map(draft);
        }, ct);
    }

    public async Task<NoticeDraftResponse?> DismissAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        return await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var draft = await BaseQuery(scope, _timeProvider.UtcNow())
                .FirstOrDefaultAsync(row => row.Id == id, innerCt);
            if (draft == null || draft.Status != "Draft") return null;

            draft.Status = "Dismissed";
            draft.DismissedAt = _timeProvider.UtcNow();
            draft.UpdatedAt = draft.DismissedAt.Value;
            await _db.SaveChangesAsync(innerCt);
            return Map(draft);
        }, ct);
    }

    private IQueryable<NoticeDraft> BaseQuery(WorkspaceReadScope scope, DateTime securityNowUtc)
    {
        var authorizedProperties = AuthorizedProperties(scope, securityNowUtc);
        return
        _db.NoticeDrafts
            .Include(draft => draft.RecipientLeaseManagementParty).ThenInclude(party => party!.Tenant)
            .Include(draft => draft.Property)
            .Include(draft => draft.LeaseManagement).ThenInclude(management => management!.Unit)
            .Where(draft =>
                draft.PortfolioId == scope.PortfolioId &&
                draft.PropertyId != null &&
                authorizedProperties.Any(property =>
                    property.Id == draft.PropertyId.Value &&
                    property.PortfolioId == draft.PortfolioId));
    }

    private IQueryable<NoticeDraftResponse> ResponseQuery(
        WorkspaceReadScope scope,
        DateTime securityNowUtc)
    {
        var authorizedProperties = AuthorizedProperties(scope, securityNowUtc);
        return
        _db.NoticeDrafts.AsNoTracking()
            .Where(draft =>
                draft.PortfolioId == scope.PortfolioId &&
                draft.PropertyId != null &&
                authorizedProperties.Any(property =>
                    property.Id == draft.PropertyId.Value &&
                    property.PortfolioId == draft.PortfolioId))
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
    }

    private IQueryable<Property> AuthorizedProperties(
        WorkspaceReadScope scope,
        DateTime securityNowUtc) =>
        _db.Properties.AsNoTracking().WhereAuthorized(
            _db,
            scope,
            CapabilityKeys.TenantNoticesManage,
            securityNowUtc);

    /// <summary>
    /// Resolves every caller-supplied relationship identifier against the same management/account
    /// row and its authorized Property in one translated SQL EXISTS. Mixed-workspace and mixed-
    /// relationship identifiers therefore fail closed before the set-based generation command.
    /// </summary>
    private Task<bool> ResolvesToAuthorizedPropertyAsync(
        WorkspaceReadScope scope,
        GenerateNoticeDraftsRequest request,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        var authorizedProperties = AuthorizedProperties(scope, securityNowUtc);
        return _db.LeaseManagements.AsNoTracking().AnyAsync(management =>
            management.PortfolioId == scope.PortfolioId &&
            authorizedProperties.Any(property =>
                property.Id == management.PropertyId &&
                property.PortfolioId == management.PortfolioId) &&
            (request.LeaseManagementId == null || management.Id == request.LeaseManagementId.Value) &&
            (request.RecipientTenantId == null || management.Parties.Any(party =>
                party.PortfolioId == management.PortfolioId &&
                party.TenantId == request.RecipientTenantId.Value)) &&
            (request.TenantAccountId == null ||
                management.TenantAccount != null &&
                management.TenantAccount.PortfolioId == management.PortfolioId &&
                management.TenantAccount.Id == request.TenantAccountId.Value) &&
            (request.TenantLedgerEntryId == null ||
                management.TenantAccount != null &&
                management.TenantAccount.PortfolioId == management.PortfolioId &&
                management.TenantAccount.LedgerEntries.Any(entry =>
                    entry.PortfolioId == management.PortfolioId &&
                    entry.Id == request.TenantLedgerEntryId.Value)), ct);
    }

    private static bool HasRelationshipFilter(GenerateNoticeDraftsRequest? request) =>
        request?.RecipientTenantId is not null ||
        request?.LeaseManagementId is not null ||
        request?.TenantAccountId is not null ||
        request?.TenantLedgerEntryId is not null;

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
