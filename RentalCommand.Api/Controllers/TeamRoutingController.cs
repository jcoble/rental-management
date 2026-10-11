using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>Administrative responsibility routing for internal team topics.</summary>
[ApiController]
[Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
[Route("api/v1/team-routing")]
public sealed class TeamRoutingController : AuthenticatedPortfolioControllerBase
{
    private readonly INotificationFoundationService _service;

    public TeamRoutingController(INotificationFoundationService service) => _service = service;

    [HttpGet]
    public Task<IReadOnlyList<TeamRoutingRuleResponse>> List(CancellationToken ct) =>
        _service.ListTeamRoutingRulesAsync(GetPortfolioId(), ct);

    [HttpPut]
    public async Task<ActionResult<TeamRoutingRuleResponse>> Replace(
        UpsertTeamRoutingRuleRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!NotificationRouteKey.TryParse(idempotencyKey, out var operationKey))
            return NotificationRouteKey.Invalid();

        return await _service.ReplaceTeamRoutingRuleAsync(
            NotificationRouteKey.Scope(GetActiveAccessContext()), request, operationKey, ct);
    }

    [HttpGet("{ruleId:int}/recipients")]
    public Task<IReadOnlyList<TeamRoutingRuleRecipientResponse>> ListRecipients(
        int ruleId,
        CancellationToken ct) =>
        _service.ListTeamRoutingRuleRecipientsAsync(GetPortfolioId(), ruleId, ct);

    [HttpGet("{ruleId:int}/preview")]
    public Task<IReadOnlyList<TeamRoutingRecipientPreview>> Preview(
        int ruleId,
        CancellationToken ct) =>
        _service.PreviewTeamRoutingAsync(GetPortfolioId(), ruleId, ct);

    [HttpGet("morning-briefing")]
    public Task<MorningBriefingSettingsResponse> GetMorningBriefing(CancellationToken ct) =>
        _service.GetMorningBriefingSettingsAsync(GetPortfolioId(), ct);

    [HttpPut("morning-briefing")]
    public async Task<ActionResult<MorningBriefingSettingsResponse>> UpdateMorningBriefing(
        UpdateMorningBriefingSettingsRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!NotificationRouteKey.TryParse(idempotencyKey, out var operationKey))
            return NotificationRouteKey.Invalid();

        return await _service.UpdateMorningBriefingSettingsAsync(
            NotificationRouteKey.Scope(GetActiveAccessContext()), request, operationKey, ct);
    }
}
