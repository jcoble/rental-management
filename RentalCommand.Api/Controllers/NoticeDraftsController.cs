using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Tenant-notice drafts generated from enabled policies and canonical lease/account facts.
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
        [FromQuery] ListQuery query,
        CancellationToken ct)
    {
        return Ok(await _service.ListAsync(GetWorkspaceReadScope(), status, query, ct));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Get(int id, CancellationToken ct)
    {
        var draft = await _service.GetAsync(GetWorkspaceReadScope(), id, ct);
        return draft == null ? NotFound(new { error = "Draft notice not found" }) : Ok(draft);
    }

    /// <summary>
    /// Runs one PostgreSQL set command for the caller's portfolio. An empty body evaluates every due
    /// enabled policy; canonical relationship/account/ledger identifiers and <c>noticeType</c> narrow
    /// the same command without loading candidate ids into application memory.
    /// </summary>
    [HttpPost("generate")]
    [ProducesResponseType(typeof(GenerateNoticeDraftsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GenerateNoticeDraftsResponse>> Generate(
        [FromBody] GenerateNoticeDraftsRequest? request,
        CancellationToken ct)
    {
        return Ok(await _service.GenerateAsync(GetWorkspaceReadScope(), request, ct));
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Update(
        int id,
        [FromBody] UpdateNoticeDraftRequest request,
        CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetWorkspaceReadScope(), id, request, ct);
        return updated == null ? NotFound(new { error = "Draft notice not found or no longer editable" }) : Ok(updated);
    }

    [HttpPost("{id:int}/dismiss")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Dismiss(int id, CancellationToken ct)
    {
        var updated = await _service.DismissAsync(GetWorkspaceReadScope(), id, ct);
        return updated == null ? NotFound(new { error = "Draft notice not found or cannot be dismissed" }) : Ok(updated);
    }
}
