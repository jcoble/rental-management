using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Read-only access to the caller's portfolio activity feed (append-only <see cref="Core.Entities.ActivityLog"/>).
/// Scope comes from the JWT <c>portfolioId</c> claim; the list supports
/// <c>?type&amp;entityType&amp;entityId&amp;skip&amp;take&amp;search&amp;sort</c> and defaults to newest-first. There
/// are no create/update/delete operations.
/// </summary>
[ApiController]
[Route("api/v1/activities")]
[Produces("application/json")]
public class ActivityController : AuthenticatedPortfolioControllerBase
{
    private readonly IActivityService _service;

    public ActivityController(IActivityService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ActivityResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ActivityResponse>>> List(
        [FromQuery] ListQuery query,
        [FromQuery] RentalActivityType? type,
        [FromQuery] string? entityType,
        [FromQuery] int? entityId,
        CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), type, entityType, entityId, query, ct);
        return Ok(items);
    }
}
