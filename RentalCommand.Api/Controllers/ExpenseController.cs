using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Imaging;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for expenses within the caller's portfolio. Scope comes from the JWT <c>portfolioId</c> claim;
/// list supports <c>?propertyId&amp;skip&amp;take&amp;search&amp;sort</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/expenses")]
[Produces("application/json")]
public class ExpenseController : AuthenticatedPortfolioControllerBase
{
    // Content types we trust to render inline (mirrors ScanController allowlist).
    private static readonly HashSet<string> InlineSafeContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/png", "image/gif", "image/webp", "image/heic"
    };

    private readonly IExpenseService _service;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    public ExpenseController(IExpenseService service, RentalCommandDbContext db, IFileStorage files)
    {
        _service = service;
        _db = db;
        _files = files;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ExpenseResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ExpenseResponse>>> List(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), propertyId, query, ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ExpenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExpenseResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Expense not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ExpenseResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExpenseResponse>> Create([FromBody] CreateExpenseRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created == null
            ? NotFound(new { error = "Referenced property, vendor, or work order not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(ExpenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExpenseResponse>> Update(int id, [FromBody] UpdateExpenseRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Expense not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Expense not found" });
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/expenses/{id}/receipt[?thumb=true]  — stream receipt file
    // -------------------------------------------------------------------------

    [HttpGet("{id:int}/receipt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReceipt(int id, [FromQuery] bool thumb = false, CancellationToken ct = default)
    {
        var portfolioId = GetPortfolioId();

        var storedFile = await _db.StoredFiles
            .AsNoTracking()
            .Where(f =>
                f.PortfolioId == portfolioId &&
                f.EntityType == "Expense" &&
                f.EntityId == id &&
                f.DeletedAt == null)
            .OrderByDescending(f => f.UploadedAt)
            .FirstOrDefaultAsync(ct);

        if (storedFile is null)
            return NotFound(new { error = "Receipt not found" });

        Stream fileStream;
        try
        {
            fileStream = await _files.DownloadAsync(storedFile.FilePath, ct);
        }
        catch
        {
            return NotFound(new { error = "File not found on storage" });
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";

        // When thumb=true and the file is an image, generate a JPEG thumbnail.
        if (thumb && storedFile.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            byte[] srcBytes;
            using (var ms = new MemoryStream())
            {
                await fileStream.CopyToAsync(ms, ct);
                srcBytes = ms.ToArray();
            }

            var thumbBytes = ThumbnailResizer.ResizeToJpeg(srcBytes);
            if (thumbBytes is not null)
            {
                Response.Headers["Cache-Control"] = "private, max-age=86400";
                Response.Headers["Content-Disposition"] = $"inline; filename=\"receipt-{id}-thumb.jpg\"";
                return File(thumbBytes, "image/jpeg");
            }

            // Fall through to serve original if resize failed.
            fileStream = new MemoryStream(srcBytes);
        }

        // Serve defensively: inline for known-safe types, octet-stream otherwise.
        if (InlineSafeContentTypes.Contains(storedFile.ContentType))
        {
            Response.Headers["Content-Disposition"] = $"inline; filename=\"receipt-{id}\"";
            return File(fileStream, storedFile.ContentType);
        }

        Response.Headers["Content-Disposition"] = $"attachment; filename=\"receipt-{id}\"";
        return File(fileStream, "application/octet-stream");
    }
}
