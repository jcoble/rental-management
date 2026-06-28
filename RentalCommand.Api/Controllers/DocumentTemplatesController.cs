using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
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
    private readonly UploadSettings _uploadSettings;

    public DocumentTemplatesController(
        IDocumentTemplateService service,
        IDocumentTemplateFieldCatalog catalog,
        IOptions<UploadSettings> uploadSettings)
    {
        _service = service;
        _catalog = catalog;
        _uploadSettings = uploadSettings.Value;
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

    [HttpGet("{id:int}/preview/leases/{leaseId:int}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "application/pdf")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PreviewLeasePdf(int id, int leaseId, CancellationToken ct)
    {
        var result = await _service.PreviewLeasePdfAsync(GetPortfolioId(), id, leaseId, ct);
        return result.Outcome switch
        {
            DocumentTemplateOperationOutcome.Success => File(
                result.Value!.PdfBytes,
                "application/pdf",
                result.Value.FileName),
            DocumentTemplateOperationOutcome.NotFound => JsonError(StatusCodes.Status404NotFound, result.Error),
            DocumentTemplateOperationOutcome.Invalid => JsonError(StatusCodes.Status400BadRequest, result.Error),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
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

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(DocumentTemplateResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DocumentTemplateResponse>> UploadPdf(
        IFormFile file,
        [FromForm] string? name,
        [FromForm] string? description,
        [FromForm] bool defaultForPortfolio,
        [FromForm] int? propertyId,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "A non-empty PDF file is required." });
        }

        var fileName = Path.GetFileName(file.FileName ?? string.Empty);
        var contentType = NormalizePdfContentType(fileName, file.ContentType);
        var uploadError = ValidatePdfUpload(fileName, contentType, file.Length);
        if (uploadError is not null)
        {
            return BadRequest(new { error = uploadError });
        }

        var templateName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(fileName)
            : name.Trim();
        if (string.IsNullOrWhiteSpace(templateName))
        {
            return BadRequest(new { error = "Template name is required." });
        }

        if (templateName.Length > 200)
        {
            return BadRequest(new { error = "Template name must be 200 characters or fewer." });
        }

        if (description?.Length > 2000)
        {
            return BadRequest(new { error = "Description must be 2,000 characters or fewer." });
        }

        await using var stream = file.OpenReadStream();
        var result = await _service.UploadPdfAsync(
            GetPortfolioId(),
            stream,
            fileName,
            contentType,
            file.Length,
            templateName,
            description,
            defaultForPortfolio,
            propertyId,
            ct);

        return result.Outcome switch
        {
            DocumentTemplateOperationOutcome.Success => CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value),
            DocumentTemplateOperationOutcome.Invalid => BadRequest(new { error = result.Error }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
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

    private static ContentResult JsonError(int statusCode, string? error) => new()
    {
        StatusCode = statusCode,
        ContentType = "application/json",
        Content = JsonSerializer.Serialize(new { error = error ?? "Request failed." })
    };

    private string? ValidatePdfUpload(string fileName, string contentType, long sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "File name is required.";
        }

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
        {
            return "File name must not contain path separators or '..'.";
        }

        var maxBytes = _uploadSettings.MaxFileSizeBytes > 0 ? _uploadSettings.MaxFileSizeBytes : 52_428_800;
        if (sizeBytes > maxBytes)
        {
            return $"File size {sizeBytes:N0} bytes exceeds the {maxBytes:N0}-byte limit.";
        }

        if (!string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "Only PDF lease templates are supported right now.";
        }

        return null;
    }

    private static string NormalizePdfContentType(string fileName, string? contentType)
    {
        var trimmed = contentType?.Trim() ?? string.Empty;
        var extension = Path.GetExtension(fileName);
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(trimmed) ||
             string.Equals(trimmed, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
        {
            return "application/pdf";
        }

        return trimmed;
    }
}
