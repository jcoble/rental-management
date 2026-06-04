using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Landlord-facing review of rental applications submitted via the public no-login link. All routes
/// are portfolio-scoped via the JWT <c>portfolioId</c> claim. Approving creates a real Tenant.
/// </summary>
[ApiController]
[Route("api/v1/applications")]
[Produces("application/json")]
public class ApplicationsController : AuthenticatedPortfolioControllerBase
{
    private readonly IApplicationService _service;

    public ApplicationsController(IApplicationService service)
    {
        _service = service;
    }

    /// <summary>Lists applications in the portfolio, newest first; optionally filtered by <c>?status=</c>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ApplicationResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ApplicationResponse>>> List(
        [FromQuery] string? status, [FromQuery] ListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), status, query, ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApplicationResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Application not found" }) : Ok(item);
    }

    /// <summary>Approves the application and creates a Tenant from its data.</summary>
    [HttpPost("{id:int}/approve")]
    [ProducesResponseType(typeof(ApproveApplicationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        try
        {
            var result = await _service.ApproveAsync(GetPortfolioId(), id, GetUserId(), ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:int}/decline")]
    [ProducesResponseType(typeof(ApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Decline(int id, [FromBody] DeclineApplicationRequest? body, CancellationToken ct)
    {
        try
        {
            var result = await _service.DeclineAsync(GetPortfolioId(), id, GetUserId(), body?.Reason, ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:int}/withdraw")]
    [ProducesResponseType(typeof(ApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Withdraw(int id, CancellationToken ct)
    {
        try
        {
            var result = await _service.WithdrawAsync(GetPortfolioId(), id, GetUserId(), ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Generates/rotates the portfolio's public application token and returns the apply path the
    /// landlord can share. Rotating invalidates any previously shared link.
    /// </summary>
    [HttpPost("link")]
    [ProducesResponseType(typeof(ApplicationLinkResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApplicationLinkResult>> GenerateLink(CancellationToken ct)
    {
        var result = await _service.GenerateLinkAsync(GetPortfolioId(), ct);
        return Ok(result);
    }
}
