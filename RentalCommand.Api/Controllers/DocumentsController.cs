using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// General-purpose document attachment hub: upload, list, download, and soft-delete
/// <see cref="Core.Entities.StoredFile"/> rows for any entity type within the caller's portfolio.
/// All routes are portfolio-scoped via the JWT <c>portfolioId</c> claim.
/// </summary>
[ApiController]
[Route("api/v1/documents")]
[Produces("application/json")]
public sealed class DocumentsController : AuthenticatedPortfolioControllerBase
{
    // Content types safe to render inline (no active content that could run scripts).
    private static readonly HashSet<string> InlineSafeContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/heic"
    };

    // Additional MIME types allowed for general documents (beyond the scan-only allowlist).
    private static readonly HashSet<string> DocumentMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Plain text / CSV
        "text/plain",
        "text/csv",
        // Word processing
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        // Spreadsheets
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        // Presentations
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    // Allowed extensions for the document hub (superset of scan extensions).
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".heic",
        ".txt", ".csv",
        ".doc", ".docx",
        ".xls", ".xlsx",
        ".ppt", ".pptx"
    };

    private readonly IDocumentService _documents;
    private readonly IFileStorage _storage;
    private readonly RentalCommandDbContext _db;
    private readonly UploadSettings _uploadSettings;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IDocumentService documents,
        IFileStorage storage,
        RentalCommandDbContext db,
        IOptions<UploadSettings> uploadSettings,
        ILogger<DocumentsController> logger)
    {
        _documents = documents;
        _storage = storage;
        _db = db;
        _uploadSettings = uploadSettings.Value;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/documents  — upload a document and attach to an entity
    // -------------------------------------------------------------------------

    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(DocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DocumentDto>> Upload(
        IFormFile file,
        [FromForm] string entityType,
        [FromForm] int entityId,
        [FromForm] string? category,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty file is required." });

        var contentType = file.ContentType?.Trim() ?? string.Empty;
        var fileName = Path.GetFileName(file.FileName ?? string.Empty);

        var (isValid, validationError) = ValidateDocumentUpload(fileName, contentType, file.Length);
        if (!isValid)
            return BadRequest(new { error = validationError });

        if (string.IsNullOrWhiteSpace(entityType))
            return BadRequest(new { error = "entityType is required." });

        var portfolioId = GetPortfolioId();
        var normalizedEntityType = entityType.Trim();

        // Cross-tenant attach guard: the referenced entity must exist inside the caller's portfolio.
        // Without this, a caller could attach a document to another tenant's record by id.
        var (entityKnown, entityInPortfolio) =
            await EntityBelongsToPortfolioAsync(normalizedEntityType, entityId, portfolioId, ct);
        if (!entityKnown)
            return BadRequest(new { error = $"entityType '{normalizedEntityType}' is not a supported document target." });
        if (!entityInPortfolio)
            return NotFound(new { error = "The referenced record was not found in your portfolio." });

        // Store the blob.
        string storageKey;
        try
        {
            storageKey = await _storage.UploadAsync(file.OpenReadStream(), fileName, contentType, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document storage upload failed for portfolio {PortfolioId}", portfolioId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "File storage failed." });
        }

        // Persist the StoredFile row; clean up the blob on DB failure.
        DocumentDto dto;
        try
        {
            dto = await _documents.CreateAsync(
                portfolioId,
                normalizedEntityType,
                entityId,
                DiskFileStorage.SanitizeFileName(fileName),
                contentType,
                file.Length,
                storageKey,
                ct);
        }
        catch
        {
            // Best-effort orphan blob cleanup.
            try { await _storage.DeleteAsync(storageKey, ct); } catch { /* swallow */ }
            throw;
        }

        dto.Category = category;
        return CreatedAtAction(nameof(GetFile), new { id = dto.Id }, dto);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/documents?entityType=&entityId=  — list documents for an entity
    // -------------------------------------------------------------------------

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DocumentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<DocumentDto>>> List(
        [FromQuery] string entityType,
        [FromQuery] int entityId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            return BadRequest(new { error = "entityType query parameter is required." });

        var docs = await _documents.ListAsync(GetPortfolioId(), entityType.Trim(), entityId, ct);
        return Ok(docs);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/documents/{id}/file  — stream the stored blob
    // -------------------------------------------------------------------------

    [HttpGet("{id:int}/file", Name = nameof(GetFile))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFile(int id, CancellationToken ct)
    {
        var row = await _documents.FindAsync(GetPortfolioId(), id, ct);
        if (row is null)
            return NotFound(new { error = "Document not found." });

        Stream stream;
        try
        {
            stream = await _storage.DownloadAsync(row.FilePath, ct);
        }
        catch
        {
            return NotFound(new { error = "File not found on storage." });
        }

        // Serve defensively: never let the browser execute active content on our origin.
        // Images and PDFs are rendered inline; everything else is forced to download.
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        var safeName = Uri.EscapeDataString(row.FileName);

        if (InlineSafeContentTypes.Contains(row.ContentType))
        {
            Response.Headers["Content-Disposition"] = $"inline; filename=\"{safeName}\"";
            return File(stream, row.ContentType);
        }

        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeName}\"";
        return File(stream, "application/octet-stream");
    }

    // -------------------------------------------------------------------------
    // DELETE /api/v1/documents/{id}  — soft-delete
    // -------------------------------------------------------------------------

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _documents.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Document not found." });
    }

    // -------------------------------------------------------------------------
    // Cross-tenant attach guard
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that <paramref name="entityType"/>/<paramref name="entityId"/> references a row that
    /// lives in <paramref name="portfolioId"/>. Matching is case-insensitive on the entity type name.
    /// </summary>
    /// <returns>
    /// <c>Known</c> = whether the entity type is a recognised document target;
    /// <c>InPortfolio</c> = whether the referenced row exists within the caller's portfolio.
    /// </returns>
    private async Task<(bool Known, bool InPortfolio)> EntityBelongsToPortfolioAsync(
        string entityType, int entityId, int portfolioId, CancellationToken ct)
    {
        // A non-positive entityId can never match a real row, so the AnyAsync checks below
        // naturally report "not in portfolio" (→ 404) for a recognised type.
        switch (entityType.ToLowerInvariant())
        {
            case "property":
                return (true, await _db.Properties.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            case "unit":
                // Unit has no direct PortfolioId; scope through its parent Property.
                return (true, await _db.Units.AnyAsync(
                    e => e.Id == entityId && e.Property != null && e.Property.PortfolioId == portfolioId, ct));
            case "tenant":
                return (true, await _db.Tenants.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            case "lease":
                return (true, await _db.Leases.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            case "payment":
                return (true, await _db.Payments.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            case "expense":
                return (true, await _db.Expenses.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            case "vendor":
                return (true, await _db.Vendors.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            case "workorder":
                return (true, await _db.WorkOrders.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            case "appointment":
                return (true, await _db.Appointments.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            case "inspection":
                return (true, await _db.Inspections.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            case "ownerentity":
                return (true, await _db.OwnerEntities.AnyAsync(
                    e => e.Id == entityId && e.PortfolioId == portfolioId, ct));
            default:
                return (false, false);
        }
    }

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    private (bool IsValid, string? Error) ValidateDocumentUpload(string? fileName, string contentType, long sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return (false, "File name is required.");

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return (false, "File name must not contain path separators or '..'.");

        var maxBytes = _uploadSettings.MaxFileSizeBytes > 0 ? _uploadSettings.MaxFileSizeBytes : 52_428_800;
        if (sizeBytes > maxBytes)
            return (false, $"File size {sizeBytes:N0} bytes exceeds the {maxBytes:N0}-byte limit.");

        // Accept anything already in the scan allowlist …
        var mimeInScanList = _uploadSettings.AllowedMimeTypes is { Length: > 0 }
            && _uploadSettings.AllowedMimeTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

        // … or in the document-specific extension list.
        var docMimeAllowed = DocumentMimeTypes.Contains(contentType);

        var ext = Path.GetExtension(fileName);
        var extAllowed = !string.IsNullOrEmpty(ext) && DocumentExtensions.Contains(ext);

        if (!mimeInScanList && !docMimeAllowed && !extAllowed)
            return (false, $"Content type '{contentType}' is not permitted for document uploads.");

        return (true, null);
    }
}
