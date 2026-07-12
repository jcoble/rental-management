using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Engine.Services;

/// <summary>Consumes one DB-side, fenced batch. It never loops portfolios to discover work.</summary>
public sealed class NoticeDraftGenerationService : INoticeDraftGenerationService
{
    private readonly RentalCommandDbContext _db;
    private readonly ITenantNoticeWorkClaimStore _claims;
    private readonly INoticeDraftService _notices;
    private readonly ILogger<NoticeDraftGenerationService> _logger;
    private readonly TimeProvider _clock;

    public NoticeDraftGenerationService(
        RentalCommandDbContext db,
        ITenantNoticeWorkClaimStore claims,
        INoticeDraftService notices,
        ILogger<NoticeDraftGenerationService> logger,
        TimeProvider clock)
    {
        _db = db; _claims = claims; _notices = notices; _logger = logger; _clock = clock;
    }

    public async Task<int> GenerateAllAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var token = Guid.NewGuid();
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        var work = await _claims.ClaimReadyAsync(owner, token, now, now.AddMinutes(5), 50, ct);
        if (work.Count == 0) return 0;

        var policyFacts = await _db.TenantNoticePolicies.AsNoTracking()
            .Where(policy => work.Select(item => item.TenantNoticePolicyId).Contains(policy.Id))
            .Select(policy => new { policy.Id, policy.AutomationKey, policy.Mode, policy.FailureBehavior })
            .ToDictionaryAsync(policy => policy.Id, ct);

        var created = 0;
        foreach (var item in work)
        {
            try
            {
                var policy = policyFacts[item.TenantNoticePolicyId];
                // The claim SQL already excluded Off and unreviewed legal Auto policies. Generation
                // remains draft-first; the explicit approval/send command owns the atomic outbox write.
                var result = await _notices.GenerateAsync(item.PortfolioId, new GenerateNoticeDraftsRequest
                {
                    LeaseManagementId = item.LeaseManagementId,
                    NoticeType = policy.AutomationKey,
                }, ct);
                var draftIds = result.Drafts.Select(draft => draft.Id).ToArray();
                if (draftIds.Length > 0)
                {
                    var templateVersionId = await _db.TenantNoticePolicies.AsNoTracking()
                        .Where(row => row.Id == item.TenantNoticePolicyId)
                        .Select(row => row.WorkspaceNoticeTemplateVersionId)
                        .SingleAsync(ct);
                    await _db.NoticeDrafts.Where(draft => draftIds.Contains(draft.Id))
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(draft => draft.TenantNoticePolicyId, item.TenantNoticePolicyId)
                            .SetProperty(draft => draft.WorkspaceNoticeTemplateVersionId, templateVersionId), ct);
                }
                created += result.CreatedCount;
                if (!await _claims.CompleteAsync(item.Id, token, ct))
                    _logger.LogWarning("Lost tenant notice work fencing token for {WorkItemId}", item.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tenant notice work {WorkItemId} failed; releasing for retry", item.Id);
                if (policyFacts[item.TenantNoticePolicyId].FailureBehavior == NoticeFailureBehavior.StopAndRequireReview)
                    await _claims.BlockAsync(item.Id, token, ct);
                else
                    await _claims.ReleaseAsync(item.Id, token, now.AddMinutes(15), ct);
            }
        }
        return created;
    }
}
