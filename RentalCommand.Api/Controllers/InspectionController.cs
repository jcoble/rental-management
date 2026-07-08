using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD + smart-checklist workflow for property inspections within the caller's portfolio. Scope comes
/// from the JWT <c>portfolioId</c> claim; list supports <c>?propertyId&amp;skip&amp;take&amp;search&amp;sort</c>.
/// Create validates the referenced property/unit/lease are in the portfolio and can materialize a
/// checklist from a template. Completing an inspection spawns a work order per failed item and generates
/// a PDF report. Inspections have no soft-delete column, so removal is a hard delete.
/// </summary>
[ApiController]
[Route("api/v1/inspections")]
[Produces("application/json")]
public class InspectionController : ManagementControllerBase
{
    private readonly IInspectionService _service;

    public InspectionController(IInspectionService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<InspectionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<InspectionResponse>>> List(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), propertyId, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(InspectionListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<InspectionListResponse>> ListPage(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), propertyId, query, ct);
        return Ok(page);
    }

    /// <summary>Available checklist templates (built-ins + any portfolio-custom), each with their items.</summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(IReadOnlyList<InspectionTemplateResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<InspectionTemplateResponse>>> Templates(CancellationToken ct)
    {
        var templates = await _service.ListTemplatesAsync(GetPortfolioId(), ct);
        return Ok(templates);
    }

    [HttpGet("templates/{templateId:int}")]
    [ProducesResponseType(typeof(InspectionTemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InspectionTemplateResponse>> Template(int templateId, CancellationToken ct)
    {
        var template = await _service.GetTemplateAsync(GetPortfolioId(), templateId, ct);
        return template == null ? NotFound(new { error = "Inspection checklist template not found" }) : Ok(template);
    }

    [HttpPost("templates")]
    [ProducesResponseType(typeof(InspectionTemplateResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<InspectionTemplateResponse>> CreateTemplate([FromBody] CreateInspectionTemplateRequest request, CancellationToken ct)
    {
        var created = await _service.CreateTemplateAsync(GetPortfolioId(), request, ct);
        return CreatedAtAction(nameof(Template), new { templateId = created.Id }, created);
    }

    [HttpPatch("templates/{templateId:int}")]
    [ProducesResponseType(typeof(InspectionTemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InspectionTemplateResponse>> UpdateTemplate(
        int templateId, [FromBody] UpdateInspectionTemplateRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateTemplateAsync(GetPortfolioId(), templateId, request, ct);
        return updated == null ? NotFound(new { error = "Inspection checklist template not found" }) : Ok(updated);
    }

    [HttpDelete("templates/{templateId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTemplate(int templateId, CancellationToken ct)
    {
        var deleted = await _service.DeleteTemplateAsync(GetPortfolioId(), templateId, ct);
        return deleted ? NoContent() : NotFound(new { error = "Inspection checklist template not found" });
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(InspectionDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InspectionDetailResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Inspection not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(InspectionDetailResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InspectionDetailResponse>> Create([FromBody] CreateInspectionRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created == null
            ? NotFound(new { error = "Referenced property, unit, lease, or template not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(InspectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InspectionResponse>> Update(int id, [FromBody] UpdateInspectionRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Inspection not found" }) : Ok(updated);
    }

    /// <summary>Add a checklist question to an editable scheduled inspection.</summary>
    [HttpPost("{id:int}/items")]
    [ProducesResponseType(typeof(InspectionItemResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InspectionItemResponse>> CreateItem(
        int id, [FromBody] CreateInspectionItemRequest request, CancellationToken ct)
    {
        var created = await _service.CreateItemAsync(GetPortfolioId(), id, request, ct);
        return created == null
            ? NotFound(new { error = "Inspection not found" })
            : CreatedAtAction(nameof(Get), new { id }, created);
    }

    /// <summary>Set a checklist item's question text, result, and/or note.</summary>
    [HttpPatch("{id:int}/items/{itemId:int}")]
    [ProducesResponseType(typeof(InspectionItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InspectionItemResponse>> UpdateItem(
        int id, int itemId, [FromBody] UpdateInspectionItemRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateItemAsync(GetPortfolioId(), id, itemId, request, ct);
        return updated == null ? NotFound(new { error = "Inspection item not found" }) : Ok(updated);
    }

    /// <summary>Delete a checklist question from an editable scheduled inspection.</summary>
    [HttpDelete("{id:int}/items/{itemId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteItem(int id, int itemId, CancellationToken ct)
    {
        var deleted = await _service.DeleteItemAsync(GetPortfolioId(), id, itemId, ct);
        return deleted ? NoContent() : NotFound(new { error = "Inspection item not found" });
    }

    /// <summary>Replace the checklist question order for an editable scheduled inspection.</summary>
    [HttpPatch("{id:int}/items/reorder")]
    [ProducesResponseType(typeof(IReadOnlyList<InspectionItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<InspectionItemResponse>>> ReorderItems(
        int id, [FromBody] ReorderInspectionItemsRequest request, CancellationToken ct)
    {
        var items = await _service.ReorderItemsAsync(GetPortfolioId(), id, request, ct);
        return items == null ? NotFound(new { error = "Inspection not found" }) : Ok(items);
    }

    /// <summary>
    /// Attach an already-uploaded photo (a StoredFile id, typically from
    /// <c>POST /api/v1/documents</c> with entityType=Inspection) to a checklist item.
    /// </summary>
    [HttpPost("{id:int}/items/{itemId:int}/photo")]
    [ProducesResponseType(typeof(InspectionItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InspectionItemResponse>> AttachItemPhoto(
        int id, int itemId, [FromBody] AttachInspectionItemPhotoRequest request, CancellationToken ct)
    {
        var updated = await _service.AttachItemPhotoAsync(GetPortfolioId(), id, itemId, request.StoredFileId, ct);
        return updated == null
            ? NotFound(new { error = "Inspection item or photo file not found in this portfolio" })
            : Ok(updated);
    }

    /// <summary>
    /// Complete the inspection: set status Completed, auto-create a work order per failed item,
    /// generate a PDF report, and return a summary (counts + created work-order ids + report file id).
    /// </summary>
    [HttpPost("{id:int}/complete")]
    [ProducesResponseType(typeof(CompleteInspectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CompleteInspectionResponse>> Complete(int id, CancellationToken ct)
    {
        var (result, error) = await _service.CompleteAsync(GetPortfolioId(), id, GetUserId(), ct);
        if (result != null)
        {
            return Ok(result);
        }
        return error == null
            ? NotFound(new { error = "Inspection not found" })
            : Conflict(new { error });
    }

    /// <summary>Download the generated PDF report (404 until the inspection is completed).</summary>
    [HttpGet("{id:int}/report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Report(int id, CancellationToken ct)
    {
        var file = await _service.GetReportAsync(GetPortfolioId(), id, ct);
        if (file == null)
        {
            return NotFound(new { error = "Report not available; complete the inspection first." });
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{file.Value.FileName}\"";
        return File(file.Value.Stream, file.Value.ContentType);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Inspection not found" });
    }
}
