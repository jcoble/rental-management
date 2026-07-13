using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for depreciable capital improvements and other capital assets within the caller's portfolio.
/// </summary>
[ApiController]
[Route("api/v1/capital-assets")]
[Produces("application/json")]
public class CapitalAssetsController : ManagementControllerBase
{
    private readonly ICapitalAssetService _service;

    public CapitalAssetsController(ICapitalAssetService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CapitalAssetResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CapitalAssetResponse>>> List(
        [FromQuery] CapitalAssetListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(CapitalAssetListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CapitalAssetListResponse>> ListPage(
        [FromQuery] CapitalAssetListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(CapitalAssetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CapitalAssetResponse>> Get(
        int id, [FromQuery] int? year, CancellationToken ct)
    {
        var item = await _service.GetAuthorizedAsync(GetWorkspaceReadScope(), id, year, ct);
        return item is null ? NotFound(new { error = "Capital asset not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(CapitalAssetResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CapitalAssetResponse>> Create(
        [FromBody] CreateCapitalAssetRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAuthorizedAsync(GetWorkspaceReadScope(), request, ct);
        return created is null
            ? NotFound(new { error = "Referenced property or unit not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(CapitalAssetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CapitalAssetResponse>> Update(
        int id, [FromBody] UpdateCapitalAssetRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAuthorizedAsync(GetWorkspaceReadScope(), id, request, ct);
        return updated is null ? NotFound(new { error = "Capital asset not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAuthorizedAsync(GetWorkspaceReadScope(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Capital asset not found" });
    }
}
