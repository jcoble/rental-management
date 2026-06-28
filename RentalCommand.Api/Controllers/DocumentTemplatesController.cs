using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Portfolio-scoped reusable document templates. TSK-201 starts with lease templates whose fields are
/// anchored onto the landlord's own PDF; later the same API supports drafted/restyled documents.
/// </summary>
[ApiController]
[Route("api/v1/document-templates")]
[Produces("application/json")]
public sealed class DocumentTemplatesController : ManagementControllerBase
{
    private readonly IDocumentTemplateService _service;
    private readonly IDocumentTemplateFieldCatalog _catalog;

    public DocumentTemplatesController(IDocumentTemplateService service, IDocumentTemplateFieldCatalog catalog)
    {
        _service = service;
        _catalog = catalog;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DocumentTemplateResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DocumentTemplateResponse>>> List(
        [FromQuery] DocumentTemplateKind? kind,
        [FromQuery] DocumentTemplateStatus? status,
        [FromQuery] ListQuery query,
        CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), kind, status, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(DocumentTemplateListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DocumentTemplateListResponse>> ListPage(
        [FromQuery] DocumentTemplateKind? kind,
        [FromQuery] DocumentTemplateStatus? status,
        [FromQuery] ListQuery query,
        CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), kind, status, query, ct);
        return Ok(page);
    }

    [HttpGet("field-catalog")]
    [ProducesResponseType(typeof(IReadOnlyList<DocumentTemplateFieldCatalogItemResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DocumentTemplateFieldCatalogItemResponse>> FieldCatalog(
        [FromQuery] DocumentTemplateKind kind = DocumentTemplateKind.Lease)
    {
        return Ok(_catalog.GetCatalog(kind));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(DocumentTemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentTemplateResponse>> Get(int id, CancellationToken ct)
    {
        var template = await _service.GetAsync(GetPortfolioId(), id, ct);
        return template is null ? NotFound(new { error = "Document template not found" }) : Ok(template);
    }

    [HttpPost]
    [ProducesResponseType(typeof(DocumentTemplateResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DocumentTemplateResponse>> Create(
        [FromBody] CreateDocumentTemplateRequest request,
        CancellationToken ct)
    {
        var result = await _service.CreateAsync(GetPortfolioId(), request, ct);
        if (result.Outcome == DocumentTemplateOperationOutcome.Invalid)
        {
            return BadRequest(new { error = result.Error });
        }

        return CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(DocumentTemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentTemplateResponse>> Update(
        int id,
        [FromBody] UpdateDocumentTemplateRequest request,
        CancellationToken ct)
    {
        var result = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return Map(result);
    }

    [HttpPost("{id:int}/fields")]
    [ProducesResponseType(typeof(DocumentTemplateFieldResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentTemplateFieldResponse>> AddField(
        int id,
        [FromBody] CreateDocumentTemplateFieldRequest request,
        CancellationToken ct)
    {
        var result = await _service.AddFieldAsync(GetPortfolioId(), id, request, ct);
        return result.Outcome switch
        {
            DocumentTemplateOperationOutcome.Success => CreatedAtAction(nameof(Get), new { id }, result.Value),
            DocumentTemplateOperationOutcome.NotFound => NotFound(new { error = result.Error }),
            DocumentTemplateOperationOutcome.Invalid => BadRequest(new { error = result.Error }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    [HttpPut("{id:int}/fields/{fieldId:int}")]
    [ProducesResponseType(typeof(DocumentTemplateFieldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentTemplateFieldResponse>> UpdateField(
        int id,
        int fieldId,
        [FromBody] UpdateDocumentTemplateFieldRequest request,
        CancellationToken ct)
    {
        var result = await _service.UpdateFieldAsync(GetPortfolioId(), id, fieldId, request, ct);
        return Map(result);
    }

    [HttpDelete("{id:int}/fields/{fieldId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteField(int id, int fieldId, CancellationToken ct)
    {
        var result = await _service.DeleteFieldAsync(GetPortfolioId(), id, fieldId, ct);
        return result.Outcome switch
        {
            DocumentTemplateOperationOutcome.Success => NoContent(),
            DocumentTemplateOperationOutcome.NotFound => NotFound(new { error = result.Error }),
            DocumentTemplateOperationOutcome.Invalid => BadRequest(new { error = result.Error }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private ActionResult<T> Map<T>(DocumentTemplateOperationResult<T> result) => result.Outcome switch
    {
        DocumentTemplateOperationOutcome.Success => Ok(result.Value),
        DocumentTemplateOperationOutcome.NotFound => NotFound(new { error = result.Error }),
        DocumentTemplateOperationOutcome.Invalid => BadRequest(new { error = result.Error }),
        _ => StatusCode(StatusCodes.Status500InternalServerError),
    };
}

