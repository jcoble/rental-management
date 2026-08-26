using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Review/edit façade over the PostgreSQL set-based tenant-notice draft command. Generation never
/// loads policies, templates, candidate ids, or relationship rows into application memory.
/// </summary>
public sealed class NoticeDraftService : INoticeDraftService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IWriteExecutor _writes;

    public NoticeDraftService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IWriteExecutor writes)
    {
        _db = db;
        _timeProvider = timeProvider;
        _writes = writes;
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
        GenerateNoticeDraftsRequest? request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNoticeDraftMutation.Command(
            scope,
            AtomicNoticeDraftOperation.Generate,
            0,
            operationKey,
            request ?? new GenerateNoticeDraftsRequest());
        var outcome = await ExecuteAsync(command, ct);
        return ReadSnapshot<GenerateNoticeDraftsResponse>(outcome.Value);
    }

    public async Task<NoticeDraftResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateNoticeDraftRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNoticeDraftMutation.Command(
            scope, AtomicNoticeDraftOperation.Update, id, operationKey, request);
        var outcome = await ExecuteAsync(command, ct);
        return outcome.Value.Found ? ReadSnapshot<NoticeDraftResponse>(outcome.Value) : null;
    }

    public async Task<NoticeDraftResponse?> DismissAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNoticeDraftMutation.Command(
            scope, AtomicNoticeDraftOperation.Dismiss, id, operationKey, new { });
        var outcome = await ExecuteAsync(command, ct);
        return outcome.Value.Found ? ReadSnapshot<NoticeDraftResponse>(outcome.Value) : null;
    }

    private Task<AtomicCommandOutcome<AtomicNoticeDraftMutationResult>> ExecuteAsync(
        AtomicNoticeDraftMutationCommand command,
        CancellationToken ct) =>
        _writes.ExecuteAsync(
            AtomicNoticeDraftMutation.Identity(command).IdempotencyKey,
            AtomicNoticeDraftMutation.Write(_db, command), ct);

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

    private static T ReadSnapshot<T>(AtomicNoticeDraftMutationResult result) where T : class =>
        JsonSerializer.Deserialize<T>(result.ResponseJson)
        ?? throw new AtomicReceiptInvariantException("The tenant notice receipt snapshot is invalid.");
}
