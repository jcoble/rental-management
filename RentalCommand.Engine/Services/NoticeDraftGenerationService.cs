using Microsoft.Extensions.Logging;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Notifications;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>Consumes one DB-side, fenced batch. It never loops portfolios to discover work.</summary>
public sealed class NoticeDraftGenerationService : INoticeDraftGenerationService
{
    private readonly ITenantNoticeWorkClaimStore _claims;
    private readonly IWriteExecutor _writes;
    private readonly RentalCommandDbContext _db;
    private readonly INotificationFoundationService _foundation;
    private readonly ILogger<NoticeDraftGenerationService> _logger;
    private readonly TimeProvider _clock;

    public NoticeDraftGenerationService(
        ITenantNoticeWorkClaimStore claims,
        IWriteExecutor writes,
        RentalCommandDbContext db,
        INotificationFoundationService foundation,
        ILogger<NoticeDraftGenerationService> logger,
        TimeProvider clock)
    {
        _claims = claims;
        _writes = writes;
        _db = db;
        _foundation = foundation;
        _logger = logger;
        _clock = clock;
    }

    public async Task<int> GenerateAllAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var token = Guid.NewGuid();
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        var work = await _claims.ClaimReadyAsync(owner, token, now, now.AddMinutes(5), 50, ct);
        if (work.Count == 0) return 0;

        var command = new ApplyClaimedTenantNoticeDraftBatchCommand(token);
        var handler = new ApplyClaimedTenantNoticeDraftBatchRule(_db);
        var outcome = await _writes.ExecuteAsync(
            TenantNoticeDraftAutomation.Identity(command).IdempotencyKey,
            TenantNoticeDraftAutomation.Write(
                command, handler.ExecuteAsync, handler.AuthorizeAsync),
            ct);
        var generated = outcome.Value.Drafts;
        var byWorkItem = generated
            .ToDictionary(row => row.WorkItemId
                ?? throw new InvalidOperationException("Claimed notice batch returned an unbound draft."));
        var created = outcome.Value.CreatedCount;

        foreach (var item in work)
        {
            var draftPersisted = false;
            try
            {
                if (!byWorkItem.TryGetValue(item.Id, out var draft))
                {
                    throw new InvalidOperationException($"Tenant notice work item {item.Id} produced no canonical draft.");
                }

                draftPersisted = true;
                if (item.IsAuto && draft.Status == "Draft")
                {
                    await _foundation.ApproveAndQueueAsync(
                        NoticeApprovalExecutionContext.ForAutomation(item.PortfolioId),
                        draft.DraftId,
                        new ApproveAndQueueNoticeRequest(EnabledChannels(item)),
                        new TenantNoticeWorkFence(item.Id, token),
                        $"tenant-notice-work:{item.Id}:approve",
                        ct);
                    continue;
                }

                // Draft-mode work stops after producing editable copy. A replay of Auto work can
                // resolve the already-approved exact draft, in which case only the fenced work closes.
                if (!await _claims.CompleteAsync(item.Id, token, ct))
                {
                    _logger.LogWarning("Lost tenant notice work fencing token for {WorkItemId}", item.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tenant notice work {WorkItemId} failed; releasing for retry", item.Id);
                await ApplyFailureBehaviorAsync(item, token, now, draftPersisted, ct);
            }
        }
        return created;
    }

    private async Task ApplyFailureBehaviorAsync(
        ClaimedTenantNoticeWorkItem item,
        Guid token,
        DateTime now,
        bool draftPersisted,
        CancellationToken ct)
    {
        var terminalAttempt = item.AttemptCount >= 3;
        if (item.FailureBehavior == nameof(NoticeFailureBehavior.StopAndRequireReview)
            || (terminalAttempt && item.FailureBehavior == nameof(NoticeFailureBehavior.RetryThenFail))
            || (terminalAttempt && !draftPersisted
                && item.FailureBehavior == nameof(NoticeFailureBehavior.RetryThenDraft)))
        {
            await _claims.BlockAsync(item.Id, token, ct);
            return;
        }

        if (terminalAttempt
            && draftPersisted
            && item.FailureBehavior == nameof(NoticeFailureBehavior.RetryThenDraft))
        {
            await _claims.CompleteAsync(item.Id, token, ct);
            return;
        }

        await _claims.ReleaseAsync(item.Id, token, now.AddMinutes(15), ct);
    }

    private static IReadOnlyList<NoticeDeliveryChannel> EnabledChannels(ClaimedTenantNoticeWorkItem item)
    {
        var channels = new List<NoticeDeliveryChannel>(4);
        if (item.SendTenantPortal) channels.Add(NoticeDeliveryChannel.TenantPortal);
        if (item.SendMobilePush) channels.Add(NoticeDeliveryChannel.MobilePush);
        if (item.SendEmail) channels.Add(NoticeDeliveryChannel.Email);
        if (item.SendSms) channels.Add(NoticeDeliveryChannel.Sms);
        return channels;
    }
}
