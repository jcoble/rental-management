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
    public async Task<ActionResult<MyAlertsResponse>> UpdateMyAlerts(
        UpdateMyAlertsRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        return await _service.UpdateMyAlertsAsync(GetMutationScope(), request, operationKey, ct);
    }

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("morning-briefing")]
    public Task<MorningBriefingSettingsResponse> GetMorningBriefingSettings(CancellationToken ct) =>
        _service.GetMorningBriefingSettingsAsync(GetPortfolioId(), ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPut("morning-briefing")]
    public async Task<ActionResult<MorningBriefingSettingsResponse>> UpdateMorningBriefingSettings(
        UpdateMorningBriefingSettingsRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        return await _service.UpdateMorningBriefingSettingsAsync(
            GetMutationScope(), request, operationKey, ct);
    }

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("team-routing")]
    public Task<IReadOnlyList<TeamRoutingRuleResponse>> ListTeamRouting(CancellationToken ct) =>
        _service.ListTeamRoutingRulesAsync(GetPortfolioId(), ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPut("team-routing")]
    public async Task<ActionResult<TeamRoutingRuleResponse>> ReplaceTeamRouting(
        UpsertTeamRoutingRuleRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        return await _service.ReplaceTeamRoutingRuleAsync(GetMutationScope(), request, operationKey, ct);
    }

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
        string automationKey,
        UpsertTenantNoticePolicyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!string.Equals(automationKey, request.AutomationKey, StringComparison.Ordinal))
            return BadRequest();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        return await _service.UpsertTenantNoticePolicyAsync(GetMutationScope(), request, operationKey, ct);
    }

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("tenant-notices/templates")]
    public Task<IReadOnlyList<WorkspaceNoticeTemplateResponse>> ListTemplates(CancellationToken ct) =>
        _service.ListTemplatesAsync(GetPortfolioId(), ct);

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPost("tenant-notices/templates/seed")]
    public async Task<IActionResult> SeedTemplates(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        await _service.SeedSuppliedTemplatesAsync(GetMutationScope(), operationKey, ct);
        return NoContent();
    }

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPost("tenant-notices/templates/{systemKey}/versions")]
    public async Task<ActionResult<TenantNoticePolicyResponse>> CreateTemplateVersion(
        string systemKey,
        CreateWorkspaceNoticeTemplateVersionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        return await _service.CreateTemplateVersionAsync(
            GetMutationScope(), systemKey, request, operationKey, ct);
    }

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpPost("tenant-notices/templates/{systemKey}/restore-default")]
    public async Task<ActionResult<TenantNoticePolicyResponse>> RestoreDefault(
        string systemKey,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        return await _service.RestoreDefaultAsync(GetMutationScope(), systemKey, operationKey, ct);
    }

    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
    [HttpGet("tenant-notices/deliveries")]
    public Task<IReadOnlyList<NoticeDeliveryStatusResponse>> ListDeliveryStatuses(
        [FromQuery] int take = 50,
        CancellationToken ct = default) =>
        _service.ListDeliveryStatusesAsync(GetPortfolioId(), take, ct);

    private WorkspaceReadScope GetMutationScope()
    {
        var active = GetActiveAccessContext();
        return new WorkspaceReadScope(active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
    }

    private static bool TryValidateIdempotencyKey(string? raw, out string key)
    {
        key = raw?.Trim() ?? string.Empty;
        return key.Length is > 0 and <= 128;
    }

    private BadRequestObjectResult InvalidKey() =>
        BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
}
