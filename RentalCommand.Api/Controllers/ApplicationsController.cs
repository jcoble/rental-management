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
public class ApplicationsController : ManagementControllerBase
{
    private readonly IApplicationService _service;
    private readonly IScreeningService _screening;

    public ApplicationsController(IApplicationService service, IScreeningService screening)
    {
        _service = service;
        _screening = screening;
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

    [HttpGet("page")]
    [ProducesResponseType(typeof(ApplicationListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApplicationListResponse>> ListPage(
        [FromQuery] string? status, [FromQuery] ListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), status, query, ct);
        return Ok(page);
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
    /// Runs a background/credit screening (FCRA) for the application. Requires recorded FCRA consent
    /// (400 otherwise). The screening provider is gated: when no key is configured this returns 503
    /// "screening not configured" and records nothing. On success a screening result is created and the
    /// application moves to UnderReview.
    /// </summary>
    [HttpPost("{id:int}/screen")]
    [ProducesResponseType(typeof(ScreeningResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Screen(int id, CancellationToken ct)
    {
        try
        {
            var result = await _screening.RequestScreeningAsync(GetPortfolioId(), id, GetUserId(), ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (ConsentRequiredException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ScreeningNotConfiguredException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>Returns the screening result(s) recorded for the application, newest first.</summary>
    [HttpGet("{id:int}/screening")]
    [ProducesResponseType(typeof(IReadOnlyList<ScreeningResultResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScreening(int id, CancellationToken ct)
    {
        var results = await _screening.GetScreeningResultsAsync(GetPortfolioId(), id, ct);
        return results == null ? NotFound(new { error = "Application not found" }) : Ok(results);
    }

    /// <summary>
    /// Generates an FCRA adverse-action (denial) notice PDF for the application, stores it, and
    /// (optionally) emails it to the applicant. Pairs with declining the application.
    /// </summary>
    [HttpPost("{id:int}/adverse-action")]
    [ProducesResponseType(typeof(AdverseActionNoticeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateAdverseAction(
        int id, [FromBody] GenerateAdverseActionRequest? body, CancellationToken ct)
    {
        var result = await _screening.GenerateAdverseActionAsync(
            GetPortfolioId(), id, GetUserId(), body ?? new GenerateAdverseActionRequest(), ct);
        return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
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
