using System.Text.Json;
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
    public async Task<ApplyClaimedTenantNoticeDraftBatchResult> HandleAsync(
        ApplyClaimedTenantNoticeDraftBatchCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.ClaimToken == Guid.Empty)
        {
            throw new ArgumentException("A tenant-notice draft claim token is required.", nameof(command));
        }

        var generated = await attempt.NoticeDrafts.GenerateClaimedBatchAsync(command.ClaimToken, ct);
        var createdCount = generated.FirstOrDefault()?.CreatedCount ?? 0;
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        foreach (var draft in generated)
        {
            var operation = draft.WasCreated ? AuditLogOperation.Created : AuditLogOperation.Updated;
            var operationText = draft.WasCreated ? "create" : "resolve";

            attempt.StageSemanticEvent(new AtomicSemanticAudit(
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
                now);
            attempt.StageOutbox(new OutboxMessage
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
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        return new ApplyClaimedTenantNoticeDraftBatchResult(createdCount, generated.ToArray());
    }
}
