using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Lease Lifecycle Autopilot: drafts renewal offers, late-rent notices, and move-out reminders
/// for landlord review. Approval sends through the existing tenant conversation channel fanout.
/// </summary>
[ApiController]
[Route("api/v1/notices")]
[Produces("application/json")]
public class NoticeDraftsController : AuthenticatedPortfolioControllerBase
{
    private readonly INoticeDraftService _service;

    public NoticeDraftsController(INoticeDraftService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<NoticeDraftResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NoticeDraftResponse>>> List(
        [FromQuery] string? status,
        CancellationToken ct)
    {
        return Ok(await _service.ListAsync(GetPortfolioId(), status, ct));
    }

    [HttpPost("generate")]
    [ProducesResponseType(typeof(GenerateNoticeDraftsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GenerateNoticeDraftsResponse>> Generate(CancellationToken ct)
    {
        return Ok(await _service.GenerateAsync(GetPortfolioId(), ct));
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Update(
        int id,
        [FromBody] UpdateNoticeDraftRequest request,
        CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Draft notice not found or no longer editable" }) : Ok(updated);
    }

    [HttpPost("{id:int}/approve")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Approve(
        int id,
        [FromBody] ApproveNoticeDraftRequest request,
        CancellationToken ct)
    {
        var updated = await _service.ApproveAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Draft notice not found or cannot be approved" }) : Ok(updated);
    }

    [HttpPost("{id:int}/dismiss")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Dismiss(int id, CancellationToken ct)
    {
        var updated = await _service.DismissAsync(GetPortfolioId(), id, ct);
        return updated == null ? NotFound(new { error = "Draft notice not found or cannot be dismissed" }) : Ok(updated);
    }
}
