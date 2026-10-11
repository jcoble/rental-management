using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>Workspace-level scheduled financial automation controls.</summary>
[ApiController]
[Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage)]
[Route("api/v1/automation-settings")]
public sealed class AutomationSettingsController : AuthenticatedPortfolioControllerBase
{
    private readonly INotificationFoundationService _service;

    public AutomationSettingsController(INotificationFoundationService service) => _service = service;

    [HttpGet("late-fees")]
    public Task<LateFeeAutomationSettingsResponse> GetLateFees(CancellationToken ct) =>
        _service.GetLateFeeAutomationSettingsAsync(GetPortfolioId(), ct);

    [HttpPut("late-fees")]
    public async Task<ActionResult<LateFeeAutomationSettingsResponse>> UpdateLateFees(
        UpdateLateFeeAutomationSettingsRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!NotificationRouteKey.TryParse(idempotencyKey, out var operationKey))
            return NotificationRouteKey.Invalid();

        return await _service.UpdateLateFeeAutomationSettingsAsync(
            NotificationRouteKey.Scope(GetActiveAccessContext()), request, operationKey, ct);
    }
}
