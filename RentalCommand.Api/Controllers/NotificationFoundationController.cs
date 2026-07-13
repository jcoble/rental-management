using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/notification-settings")]
public sealed class NotificationFoundationController : AuthenticatedPortfolioControllerBase
{
    private readonly INotificationFoundationService _service;
    public NotificationFoundationController(INotificationFoundationService service) => _service = service;

    [HttpGet("my-alerts")]
    public Task<MyAlertsResponse> GetMyAlerts(CancellationToken ct) =>
        _service.GetMyAlertsAsync(GetPortfolioId(), GetUserId(), ct);

    [HttpPut("my-alerts")]
    public Task<MyAlertsResponse> UpdateMyAlerts(UpdateMyAlertsRequest request, CancellationToken ct) =>
        _service.UpdateMyAlertsAsync(GetPortfolioId(), GetUserId(), request, ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("morning-briefing")]
    public Task<MorningBriefingSettingsResponse> GetMorningBriefingSettings(CancellationToken ct) =>
        _service.GetMorningBriefingSettingsAsync(GetPortfolioId(), ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPut("morning-briefing")]
    public Task<MorningBriefingSettingsResponse> UpdateMorningBriefingSettings(
        UpdateMorningBriefingSettingsRequest request, CancellationToken ct) =>
        _service.UpdateMorningBriefingSettingsAsync(GetPortfolioId(), request, ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("team-routing")]
    public Task<IReadOnlyList<TeamRoutingRuleResponse>> ListTeamRouting(CancellationToken ct) =>
        _service.ListTeamRoutingRulesAsync(GetPortfolioId(), ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPut("team-routing")]
    public Task<TeamRoutingRuleResponse> ReplaceTeamRouting(UpsertTeamRoutingRuleRequest request, CancellationToken ct) =>
        _service.ReplaceTeamRoutingRuleAsync(GetPortfolioId(), request, ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("team-routing/{ruleId:int}/recipients")]
    public Task<IReadOnlyList<TeamRoutingRuleRecipientResponse>> ListTeamRoutingRecipients(
        int ruleId, CancellationToken ct) =>
        _service.ListTeamRoutingRuleRecipientsAsync(GetPortfolioId(), ruleId, ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("team-routing/{ruleId:int}/preview")]
    public Task<IReadOnlyList<TeamRoutingRecipientPreview>> PreviewTeamRouting(int ruleId, CancellationToken ct) =>
        _service.PreviewTeamRoutingAsync(GetPortfolioId(), ruleId, ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("tenant-notices")]
    public Task<IReadOnlyList<TenantNoticePolicyResponse>> ListTenantNoticePolicies(CancellationToken ct) =>
        _service.ListTenantNoticePoliciesAsync(GetPortfolioId(), ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPut("tenant-notices/{automationKey}")]
    public async Task<ActionResult<TenantNoticePolicyResponse>> UpsertTenantNoticePolicy(
        string automationKey, UpsertTenantNoticePolicyRequest request, CancellationToken ct)
    {
        if (!string.Equals(automationKey, request.AutomationKey, StringComparison.Ordinal))
            return BadRequest();
        return await _service.UpsertTenantNoticePolicyAsync(GetPortfolioId(), GetUserId(), request, ct);
    }

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("tenant-notices/templates")]
    public Task<IReadOnlyList<WorkspaceNoticeTemplateResponse>> ListTemplates(CancellationToken ct) =>
        _service.ListTemplatesAsync(GetPortfolioId(), ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPost("tenant-notices/templates/seed")]
    public async Task<IActionResult> SeedTemplates(CancellationToken ct)
    {
        await _service.SeedSuppliedTemplatesAsync(GetPortfolioId(), GetUserId(), ct);
        return NoContent();
    }

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPost("tenant-notices/templates/{systemKey}/versions")]
    public Task<TenantNoticePolicyResponse> CreateTemplateVersion(
        string systemKey, CreateWorkspaceNoticeTemplateVersionRequest request, CancellationToken ct) =>
        _service.CreateTemplateVersionAsync(GetPortfolioId(), GetUserId(), systemKey, request, ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPost("tenant-notices/templates/{systemKey}/restore-default")]
    public Task<TenantNoticePolicyResponse> RestoreDefault(string systemKey, CancellationToken ct) =>
        _service.RestoreDefaultAsync(GetPortfolioId(), GetUserId(), systemKey, ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPost("tenant-notices/drafts/{draftId:int}/approve-and-queue")]
    public async Task<ActionResult<object>> ApproveAndQueue(
        int draftId, ApproveAndQueueNoticeRequest request, CancellationToken ct)
    {
        var renderedNoticeId = await _service.ApproveAndQueueAsync(
            GetPortfolioId(), GetUserId(), draftId, request, null, ct);
        return Ok(new { renderedNoticeId });
    }

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("tenant-notices/deliveries")]
    public Task<IReadOnlyList<NoticeDeliveryStatusResponse>> ListDeliveryStatuses(
        [FromQuery] int take = 50,
        CancellationToken ct = default) =>
        _service.ListDeliveryStatusesAsync(GetPortfolioId(), take, ct);
}
