using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>Independent tenant automation policies, templates, and delivery evidence.</summary>
[ApiController]
[Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
[Route("api/v1/tenant-notices")]
public sealed class TenantNoticePoliciesController : AuthenticatedPortfolioControllerBase
{
    private readonly INotificationFoundationService _service;

    public TenantNoticePoliciesController(INotificationFoundationService service) => _service = service;

    [HttpGet]
    public Task<IReadOnlyList<TenantNoticePolicyResponse>> ListPolicies(CancellationToken ct) =>
        _service.ListTenantNoticePoliciesAsync(GetPortfolioId(), ct);

    [HttpPut("{automationKey}")]
    public async Task<ActionResult<TenantNoticePolicyResponse>> UpsertPolicy(
        string automationKey,
        UpsertTenantNoticePolicyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!string.Equals(automationKey, request.AutomationKey, StringComparison.Ordinal))
            return BadRequest();
        if (!NotificationRouteKey.TryParse(idempotencyKey, out var operationKey))
            return NotificationRouteKey.Invalid();

        return await _service.UpsertTenantNoticePolicyAsync(
            NotificationRouteKey.Scope(GetActiveAccessContext()), request, operationKey, ct);
    }

    [HttpGet("templates")]
    public Task<IReadOnlyList<WorkspaceNoticeTemplateResponse>> ListTemplates(CancellationToken ct) =>
        _service.ListTemplatesAsync(GetPortfolioId(), ct);

    [HttpPost("templates/{systemKey}/versions")]
    public async Task<ActionResult<TenantNoticePolicyResponse>> CreateTemplateVersion(
        string systemKey,
        CreateWorkspaceNoticeTemplateVersionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!NotificationRouteKey.TryParse(idempotencyKey, out var operationKey))
            return NotificationRouteKey.Invalid();

        return await _service.CreateTemplateVersionAsync(
            NotificationRouteKey.Scope(GetActiveAccessContext()), systemKey, request, operationKey, ct);
    }

    [HttpPost("templates/{systemKey}/restore-default")]
    public async Task<ActionResult<TenantNoticePolicyResponse>> RestoreDefault(
        string systemKey,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!NotificationRouteKey.TryParse(idempotencyKey, out var operationKey))
            return NotificationRouteKey.Invalid();

        return await _service.RestoreDefaultAsync(
            NotificationRouteKey.Scope(GetActiveAccessContext()), systemKey, operationKey, ct);
    }

    [HttpGet("deliveries")]
    public Task<IReadOnlyList<NoticeDeliveryStatusResponse>> ListDeliveryStatuses(
        [FromQuery] int take = 50,
        CancellationToken ct = default) =>
        _service.ListDeliveryStatusesAsync(GetPortfolioId(), take, ct);
}
