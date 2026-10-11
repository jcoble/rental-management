using RentalCommand.Core.Atomic;
using RentalCommand.Data.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Notifications;
using RentalCommand.Data.Notifications;
using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data.Notifications;

/// <summary>Admits the existing one-statement notice generator into the active receipt transaction.</summary>
public sealed class AtomicNoticeDraftPersistence
{
    private readonly TenantNoticeDraftSetStore _store;
    private readonly AtomicAuditScope _scope;

    private AtomicNoticeDraftPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
    {
        _store = new TenantNoticeDraftSetStore(db);
        _scope = scope;
    }


    private static AtomicAuditScope RequireAuditScope(RentalCommandDbContext db, IAtomicCommandContext context)
    {
        if (context is not AtomicCommandContext owner || !owner.Owns(db))
        {
            throw new AtomicArchitectureException(
                "Atomic helper requires the exact scoped DbContext and active command context.");
        }

        return owner.AuditScope;
    }
public static Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateClaimedBatchAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        Guid claimToken,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicNoticeDraftPersistence(db, scope).GenerateClaimedBatchAsync(claimToken, ct);
    }

    public static Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateManualAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        WorkspaceReadScope scope,
        int? recipientTenantId,
        int? leaseManagementId,
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        string? noticeType,
        DateTime securityNowUtc,
        CancellationToken ct = default)
    {
        var auditScope = RequireAuditScope(db, context);
        return new AtomicNoticeDraftPersistence(db, auditScope).GenerateManualAsync(
            scope, recipientTenantId, leaseManagementId, tenantAccountId,
            tenantLedgerEntryId, noticeType, securityNowUtc, ct);
    }

    public static Task CompleteApprovalAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        int? conversationId,
        string approvedChannels,
        DateTime approvedAtUtc,
        DateTime updatedAtUtc,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicNoticeDraftPersistence(db, scope).CompleteApprovalAsync(
            portfolioId, noticeDraftId, renderedNoticeId, conversationId,
            approvedChannels, approvedAtUtc, updatedAtUtc, ct);
    }

    public static Task<int> ReconcileApprovalNotificationIntentAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        string renderedSubject,
        string renderedMessagePreview,
        DateTime renderedApprovedAtUtc,
        int[] expectedLeaseManagementPartyIds,
        NoticeDeliveryChannel[] expectedChannels,
        int[] expectedRecipientUserIds,
        int[] expectedNavigationAccessContextIds,
        long[] expectedNavigationAccessRevisions,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicNoticeDraftPersistence(db, scope).ReconcileApprovalNotificationIntentAsync(
            portfolioId, noticeDraftId, renderedNoticeId, renderedSubject, renderedMessagePreview,
            renderedApprovedAtUtc, expectedLeaseManagementPartyIds, expectedChannels,
            expectedRecipientUserIds, expectedNavigationAccessContextIds,
            expectedNavigationAccessRevisions, ct);
    }

    public static Task<int> ReconcileApprovedDeliveryChronologyAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        DateTime existingApprovedAtUtc,
        DateTime correctedApprovedAtUtc,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicNoticeDraftPersistence(db, scope).ReconcileApprovedDeliveryChronologyAsync(
            portfolioId, noticeDraftId, renderedNoticeId, existingApprovedAtUtc,
            correctedApprovedAtUtc, ct);
    }

    public static Task<AtomicNoticeApprovalCompletionValidation> ValidateApprovalCompletionAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        string renderedSubject,
        string renderedMessagePreview,
        DateTime renderedApprovedAtUtc,
        string approvedChannels,
        int? expectedConversationId,
        int recipientLeaseManagementPartyId,
        int[] expectedLeaseManagementPartyIds,
        NoticeDeliveryChannel[] expectedChannels,
        string[] expectedDestinations,
        string[] expectedOutboxIdempotencyKeys,
        int[] expectedRecipientUserIds,
        int[] expectedNavigationAccessContextIds,
        long[] expectedNavigationAccessRevisions,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicNoticeDraftPersistence(db, scope).ValidateApprovalCompletionAsync(
            portfolioId, noticeDraftId, renderedNoticeId, renderedSubject,
            renderedMessagePreview, renderedApprovedAtUtc, approvedChannels,
            expectedConversationId, recipientLeaseManagementPartyId,
            expectedLeaseManagementPartyIds, expectedChannels, expectedDestinations,
            expectedOutboxIdempotencyKeys, expectedRecipientUserIds,
            expectedNavigationAccessContextIds, expectedNavigationAccessRevisions, ct);
    }

    public static Task<AtomicNoticeApprovalCompletionValidation> ValidateApprovalRecoveryCandidateAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        string renderedSubject,
        string renderedMessagePreview,
        DateTime renderedApprovedAtUtc,
        string approvedChannels,
        int? expectedConversationId,
        int recipientLeaseManagementPartyId,
        int[] expectedLeaseManagementPartyIds,
        NoticeDeliveryChannel[] expectedChannels,
        string[] expectedDestinations,
        string[] expectedOutboxIdempotencyKeys,
        int[] expectedRecipientUserIds,
        int[] expectedNavigationAccessContextIds,
        long[] expectedNavigationAccessRevisions,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicNoticeDraftPersistence(db, scope).ValidateApprovalRecoveryCandidateAsync(
            portfolioId, noticeDraftId, renderedNoticeId, renderedSubject,
            renderedMessagePreview, renderedApprovedAtUtc, approvedChannels,
            expectedConversationId, recipientLeaseManagementPartyId,
            expectedLeaseManagementPartyIds, expectedChannels, expectedDestinations,
            expectedOutboxIdempotencyKeys, expectedRecipientUserIds,
            expectedNavigationAccessContextIds, expectedNavigationAccessRevisions, ct);
    }

    public async Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateClaimedBatchAsync(
        Guid claimToken,
        CancellationToken ct = default)
    {
        using var lease = _scope.BeginInternalRawDml("NoticeDrafts", AtomicRawDmlOperation.Insert);
        return await _store.GenerateClaimedBatchAsync(claimToken, ct);
    }

    public async Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateManualAsync(
        WorkspaceReadScope scope,
        int? recipientTenantId,
        int? leaseManagementId,
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        string? noticeType,
        DateTime securityNowUtc,
        CancellationToken ct = default)
    {
        using var lease = _scope.BeginInternalRawDml("NoticeDrafts", AtomicRawDmlOperation.Insert);
        return await _store.GenerateManualAsync(
            scope,
            recipientTenantId,
            leaseManagementId,
            tenantAccountId,
            tenantLedgerEntryId,
            noticeType,
            securityNowUtc,
            ct);
    }

    public async Task CompleteApprovalAsync(
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        int? conversationId,
        string approvedChannels,
        DateTime approvedAtUtc,
        DateTime updatedAtUtc,
        CancellationToken ct = default)
    {
        using var lease = _scope.BeginInternalRawDml("NoticeDrafts", AtomicRawDmlOperation.Update);
        var affected = await _store.CompleteApprovalAsync(
            portfolioId,
            noticeDraftId,
            renderedNoticeId,
            conversationId,
            approvedChannels,
            approvedAtUtc,
            updatedAtUtc,
            ct);
        if (affected != 1)
        {
            throw new DbUpdateConcurrencyException(
                $"Notice draft {noticeDraftId} is no longer in Draft status.");
        }
    }

    public async Task<int> ReconcileApprovalNotificationIntentAsync(
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        string renderedSubject,
        string renderedMessagePreview,
        DateTime renderedApprovedAtUtc,
        int[] expectedLeaseManagementPartyIds,
        NoticeDeliveryChannel[] expectedChannels,
        int[] expectedRecipientUserIds,
        int[] expectedNavigationAccessContextIds,
        long[] expectedNavigationAccessRevisions,
        CancellationToken ct = default)
    {
        using var lease = _scope.BeginInternalRawDml("Notifications", AtomicRawDmlOperation.Update);
        return await _store.ReconcileApprovalNotificationIntentAsync(
            portfolioId,
            noticeDraftId,
            renderedNoticeId,
            renderedSubject,
            renderedMessagePreview,
            renderedApprovedAtUtc,
            expectedLeaseManagementPartyIds,
            expectedChannels,
            expectedRecipientUserIds,
            expectedNavigationAccessContextIds,
            expectedNavigationAccessRevisions,
            ct);
    }

    public async Task<int> ReconcileApprovedDeliveryChronologyAsync(
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        DateTime existingApprovedAtUtc,
        DateTime correctedApprovedAtUtc,
        CancellationToken ct = default)
    {
        using var lease = _scope.BeginInternalRawDmlBatch(
            new AtomicRawDmlTarget("NoticeDrafts", AtomicRawDmlOperation.Update),
            new AtomicRawDmlTarget("RenderedNotices", AtomicRawDmlOperation.Update),
            new AtomicRawDmlTarget("Notifications", AtomicRawDmlOperation.Update));
        return await _store.ReconcileApprovedDeliveryChronologyAsync(
            portfolioId,
            noticeDraftId,
            renderedNoticeId,
            existingApprovedAtUtc,
            correctedApprovedAtUtc,
            ct);
    }

    public Task<AtomicNoticeApprovalCompletionValidation> ValidateApprovalCompletionAsync(
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        string renderedSubject,
        string renderedMessagePreview,
        DateTime renderedApprovedAtUtc,
        string approvedChannels,
        int? expectedConversationId,
        int recipientLeaseManagementPartyId,
        int[] expectedLeaseManagementPartyIds,
        NoticeDeliveryChannel[] expectedChannels,
        string[] expectedDestinations,
        string[] expectedOutboxIdempotencyKeys,
        int[] expectedRecipientUserIds,
        int[] expectedNavigationAccessContextIds,
        long[] expectedNavigationAccessRevisions,
        CancellationToken ct = default) =>
        _store.ValidateApprovalCompletionAsync(
            portfolioId,
            noticeDraftId,
            renderedNoticeId,
            renderedSubject,
            renderedMessagePreview,
            renderedApprovedAtUtc,
            approvedChannels,
            expectedConversationId,
            recipientLeaseManagementPartyId,
            expectedLeaseManagementPartyIds,
            expectedChannels,
            expectedDestinations,
            expectedOutboxIdempotencyKeys,
            expectedRecipientUserIds,
            expectedNavigationAccessContextIds,
            expectedNavigationAccessRevisions,
            ct);

    public Task<AtomicNoticeApprovalCompletionValidation> ValidateApprovalRecoveryCandidateAsync(
        int portfolioId,
        int noticeDraftId,
        long renderedNoticeId,
        string renderedSubject,
        string renderedMessagePreview,
        DateTime renderedApprovedAtUtc,
        string approvedChannels,
        int? expectedConversationId,
        int recipientLeaseManagementPartyId,
        int[] expectedLeaseManagementPartyIds,
        NoticeDeliveryChannel[] expectedChannels,
        string[] expectedDestinations,
        string[] expectedOutboxIdempotencyKeys,
        int[] expectedRecipientUserIds,
        int[] expectedNavigationAccessContextIds,
        long[] expectedNavigationAccessRevisions,
        CancellationToken ct = default) =>
        _store.ValidateApprovalRecoveryCandidateAsync(
            portfolioId,
            noticeDraftId,
            renderedNoticeId,
            renderedSubject,
            renderedMessagePreview,
            renderedApprovedAtUtc,
            approvedChannels,
            expectedConversationId,
            recipientLeaseManagementPartyId,
            expectedLeaseManagementPartyIds,
            expectedChannels,
            expectedDestinations,
            expectedOutboxIdempotencyKeys,
            expectedRecipientUserIds,
            expectedNavigationAccessContextIds,
            expectedNavigationAccessRevisions,
            ct);
}
