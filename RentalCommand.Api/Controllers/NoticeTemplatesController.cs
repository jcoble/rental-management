using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Portfolio-scoped CRUD for landlord-authored notice templates (one active template per notice type).
/// Scope comes from the JWT portfolioId claim, matching <see cref="NoticeDraftsController"/>.
/// </summary>
[ApiController]
[Route("api/v1/notices/templates")]
[Produces("application/json")]
public class NoticeTemplatesController : ManagementControllerBase
{
    private readonly INoticeTemplateService _service;

    public NoticeTemplatesController(INoticeTemplateService service) => _service = service;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<NoticeTemplateResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NoticeTemplateResponse>>> List(CancellationToken ct)
        => Ok(await _service.ListAsync(GetPortfolioId(), ct));

    [HttpGet("{type}")]
    [ProducesResponseType(typeof(NoticeTemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NoticeTemplateResponse>> Get(string type, CancellationToken ct)
    {
        try { return Ok(await _service.GetAsync(GetPortfolioId(), type, ct)); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
    }

    [HttpPut("{type}")]
    [ProducesResponseType(typeof(NoticeTemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NoticeTemplateResponse>> Upsert(string type, [FromBody] UpsertNoticeTemplateRequest request, CancellationToken ct)
    {
        try { return Ok(await _service.UpsertAsync(GetPortfolioId(), type, request, ct)); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
    }
}
