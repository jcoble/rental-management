using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>Personal delivery destinations for only the signed-in account.</summary>
[ApiController]
[Route("api/v1/my-alerts")]
public sealed class MyAlertsController : AuthenticatedPortfolioControllerBase
{
    private readonly INotificationFoundationService _service;

    public MyAlertsController(INotificationFoundationService service) => _service = service;

    [HttpGet]
    public Task<MyAlertsResponse> Get(CancellationToken ct) =>
        _service.GetMyAlertsAsync(GetPortfolioId(), GetUserId(), ct);

    [HttpPut]
    public async Task<ActionResult<MyAlertsResponse>> Update(
        UpdateMyAlertsRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!NotificationRouteKey.TryParse(idempotencyKey, out var operationKey))
            return NotificationRouteKey.Invalid();

        return await _service.UpdateMyAlertsAsync(
            NotificationRouteKey.Scope(GetActiveAccessContext()), request, operationKey, ct);
    }
}
