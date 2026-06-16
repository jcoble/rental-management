using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for showings/move-in/move-out and other appointments within the caller's portfolio. Scope comes
/// from the JWT <c>portfolioId</c> claim; list supports <c>?propertyId&amp;tenantId&amp;skip&amp;take&amp;search&amp;sort</c>.
/// Create validates the referenced property/unit/lease/tenant are in the portfolio. Appointments have no
/// soft-delete column, so removal is a hard delete.
/// </summary>
[ApiController]
[Route("api/v1/appointments")]
[Produces("application/json")]
public class AppointmentController : ManagementControllerBase
{
    private readonly IAppointmentService _service;

    public AppointmentController(IAppointmentService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AppointmentResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AppointmentResponse>>> List(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, [FromQuery] int? tenantId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), propertyId, tenantId, query, ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(AppointmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AppointmentResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Appointment not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(AppointmentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AppointmentResponse>> Create([FromBody] CreateAppointmentRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created == null
            ? NotFound(new { error = "Referenced property, unit, lease, or tenant not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(AppointmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AppointmentResponse>> Update(int id, [FromBody] UpdateAppointmentRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Appointment not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Appointment not found" });
    }
}
