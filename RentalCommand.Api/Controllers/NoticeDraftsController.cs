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
public class NoticeDraftsController : ManagementControllerBase
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

    /// <summary>
    /// Generates notice drafts for the caller's portfolio. With an empty/absent body this runs
    /// portfolio-wide (every applicable relationship + open tenant charge). Scope with canonical
    /// recipient, lease-management, tenant-account, or ledger-entry identifiers and optionally
    /// <c>noticeType</c> to generate just that kind. An absent body requests portfolio-wide generation.
    /// </summary>
    [HttpPost("generate")]
    [ProducesResponseType(typeof(GenerateNoticeDraftsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GenerateNoticeDraftsResponse>> Generate(
        [FromBody] GenerateNoticeDraftsRequest? request,
        CancellationToken ct)
    {
        return Ok(await _service.GenerateAsync(GetPortfolioId(), request, ct));
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

    [HttpPost("{id:int}/dismiss")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Dismiss(int id, CancellationToken ct)
    {
        var updated = await _service.DismissAsync(GetPortfolioId(), id, ct);
        return updated == null ? NotFound(new { error = "Draft notice not found or cannot be dismissed" }) : Ok(updated);
    }
}
