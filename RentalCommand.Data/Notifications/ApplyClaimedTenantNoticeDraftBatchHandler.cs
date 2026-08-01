using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Data.Notifications;

/// <summary>
/// Atomic receipt wrapper for claimed tenant-notice draft generation. PostgreSQL still owns candidate
/// selection, rendering, dedupe, upsert, and result projection; this handler adds receipt, audit, and
/// outbox companions around that one set command.
/// </summary>
public sealed class ApplyClaimedTenantNoticeDraftBatchHandler
    : IAtomicCommandHandler<ApplyClaimedTenantNoticeDraftBatchCommand, ApplyClaimedTenantNoticeDraftBatchResult>
{
    private readonly RentalCommandDbContext _db;

    public ApplyClaimedTenantNoticeDraftBatchHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ApplyClaimedTenantNoticeDraftBatchResult> HandleAsync(
        ApplyClaimedTenantNoticeDraftBatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.ClaimToken == Guid.Empty)
        {
            throw new ArgumentException("A tenant-notice draft claim token is required.", nameof(command));
        }

        var generated = await AtomicNoticeDraftPersistence.GenerateClaimedBatchAsync(_db, context, command.ClaimToken, ct);
        var createdCount = generated.FirstOrDefault()?.CreatedCount ?? 0;
        foreach (var draft in generated)
        {
            var operation = draft.WasCreated ? AuditLogOperation.Created : AuditLogOperation.Updated;
            var operationText = draft.WasCreated ? "create" : "resolve";
            var appliedAtUtc = draft.AppliedAtUtc;

            context.StageSemanticEvent(new AtomicSemanticAudit(
                draft.PortfolioId,
                nameof(NoticeDraft),
                draft.DraftId,
                operation,
                ActorLabel: "system:tenant-notice-draft-worker",
                NewValues: JsonSerializer.Serialize(new
                {
                    draft.WorkItemId,
                    draft.LeaseManagementId,
                    draft.TenantAccountId,
                    draft.RecipientLeaseManagementPartyId,
                    draft.LeaseAgreementId,
                    draft.LeaseAddendumId,
                    draft.TenantLedgerEntryId,
                    draft.NoticeType,
                }),
                ChangeReason: draft.WasCreated
                    ? "Generated a claimed tenant notice draft."
                    : "Resolved an existing claimed tenant notice draft."),
                appliedAtUtc);
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = draft.PortfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(NoticeDraft),
                    entityId = draft.DraftId,
                    operation = operationText,
                    data = new
                    {
                        draft.WorkItemId,
                        draft.LeaseManagementId,
                        draft.TenantAccountId,
                        draft.TenantLedgerEntryId,
                        draft.NoticeType,
                    },
                }),
                IdempotencyKey = OutboxIdempotency.Create(
                    "tenant-notice-draft-worker",
                    command.ClaimToken,
                    draft.WorkItemId,
                    draft.DraftId),
                CreatedAtUtc = appliedAtUtc,
                NextAttemptAtUtc = appliedAtUtc,
            });
        }

        return new ApplyClaimedTenantNoticeDraftBatchResult(createdCount, generated.ToArray());
    }

    public async Task AuthorizeReplayAsync(
        ApplyClaimedTenantNoticeDraftBatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.ClaimToken == Guid.Empty)
        {
            throw new ArgumentException("A tenant-notice draft claim token is required.", nameof(command));
        }

        var claimedOrResolvedWorkExists = await _db.Set<TenantNoticeWorkItem>()
            .AsNoTracking()
            .AnyAsync(work =>
                work.ClaimToken == command.ClaimToken ||
                (work.Status == TenantNoticeWorkStatus.Completed &&
                 _db.Set<NoticeDraft>().Any(draft =>
                     draft.LeaseManagementId == work.LeaseManagementId &&
                     draft.RecipientLeaseManagementPartyId == work.RecipientLeaseManagementPartyId &&
                     draft.TenantLedgerEntryId == work.TenantLedgerEntryId)),
                ct);
        if (!claimedOrResolvedWorkExists)
        {
            throw new UnauthorizedAccessException("The claimed tenant-notice draft work is unavailable.");
        }
    }
}
