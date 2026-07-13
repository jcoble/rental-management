using Microsoft.Extensions.Logging;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Engine.Services;

/// <summary>Consumes one DB-side, fenced batch. It never loops portfolios to discover work.</summary>
public sealed class NoticeDraftGenerationService : INoticeDraftGenerationService
{
    private readonly ITenantNoticeWorkClaimStore _claims;
    private readonly ITenantNoticeDraftSetStore _drafts;
    private readonly INotificationFoundationService _foundation;
    private readonly ILogger<NoticeDraftGenerationService> _logger;
    private readonly TimeProvider _clock;

    public NoticeDraftGenerationService(
        ITenantNoticeWorkClaimStore claims,
        ITenantNoticeDraftSetStore drafts,
        INotificationFoundationService foundation,
        ILogger<NoticeDraftGenerationService> logger,
        TimeProvider clock)
    {
        _claims = claims;
        _drafts = drafts;
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

        var generated = await _drafts.GenerateClaimedBatchAsync(token, ct);
        var byWorkItem = generated
            .ToDictionary(row => row.WorkItemId
                ?? throw new InvalidOperationException("Claimed notice batch returned an unbound draft."));
        var created = generated.FirstOrDefault()?.CreatedCount ?? 0;

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
                        item.PortfolioId,
                        null,
                        draft.DraftId,
                        new ApproveAndQueueNoticeRequest(EnabledChannels(item)),
                        new TenantNoticeWorkFence(item.Id, token),
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
