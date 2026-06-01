using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Upload-and-scan intake endpoints. All routes are scoped to the caller's portfolio
/// via the JWT <c>portfolioId</c> claim — no <c>{portfolioId}</c> route parameter.
/// </summary>
[ApiController]
[Route("api/v1/scans")]
[Produces("application/json")]
public class ScanController : AuthenticatedPortfolioControllerBase
{
    private readonly IScanService _scan;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    // Content types we trust to render inline (non-active: no script execution). Anything else
    // is forced to download as octet-stream so an uploaded html/svg/etc. can't run on our origin.
    private static readonly HashSet<string> InlineSafeContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/png", "image/gif", "image/webp", "image/heic"
    };

    public ScanController(IScanService scan, RentalCommandDbContext db, IFileStorage files)
    {
        _scan = scan;
        _db = db;
        _files = files;
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/scans  — upload a document and create a draft
    // -------------------------------------------------------------------------

    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ScanCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ScanCreatedResponse>> Upload(
        IFormFile file,
        [FromForm] string targetEntityType,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty file is required." });

        // When a targetEntityType is explicitly provided it must be a recognised value.
        // Empty/null is allowed — the LLM worker will classify it during processing.
        if (!string.IsNullOrEmpty(targetEntityType)
            && !string.Equals(targetEntityType, "Expense", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(targetEntityType, "Payment", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = $"targetEntityType '{targetEntityType}' is not valid. Allowed values: Expense, Payment (or omit to auto-classify)." });
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        try
        {
            var draft = await _scan.CreateDraftAsync(
                GetPortfolioId(), bytes, file.ContentType, targetEntityType, ct);

            return CreatedAtAction(
                nameof(Get),
                new { id = draft.Id },
                new ScanCreatedResponse(draft.Id, draft.Status, $"/api/v1/scans/{draft.Id}/file"));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/scans/{id}  — fetch a single draft
    // -------------------------------------------------------------------------

    [HttpGet("{id:int}", Name = nameof(Get))]
    [ProducesResponseType(typeof(ScanDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScanDraftResponse>> Get(int id, CancellationToken ct)
    {
        var draft = await _db.ScanDrafts
            .FirstOrDefaultAsync(d => d.Id == id && d.PortfolioId == GetPortfolioId(), ct);

        return draft is null
            ? NotFound(new { error = "Scan draft not found" })
            : Ok(ScanDraftResponse.FromEntity(draft));
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/scans?status=&skip=&take=  — list drafts, newest first
    // -------------------------------------------------------------------------

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ScanDraftResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ScanDraftResponse>>> List(
        [FromQuery] string? status,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var portfolioId = GetPortfolioId();

        var query = _db.ScanDrafts
            .Where(d => d.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(d => d.Status == status);

        var drafts = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return Ok(drafts.Select(ScanDraftResponse.FromEntity).ToList());
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/scans/{id}/file  — stream the stored file
    // -------------------------------------------------------------------------

    [HttpGet("{id:int}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadFile(int id, [FromQuery] bool full, CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();

        var draft = await _db.ScanDrafts
            .FirstOrDefaultAsync(d => d.Id == id && d.PortfolioId == portfolioId, ct);

        if (draft is null)
            return NotFound(new { error = "Scan draft not found" });

        // Serve the small JPEG preview by default so clients (esp. phones) don't pull the
        // full-resolution original. The original is available on ?full=1. Fall back to the
        // original if the thumbnail is missing/unreadable (older scans, PDFs, etc.).
        if (!full && !string.IsNullOrEmpty(draft.ThumbnailPath))
        {
            try
            {
                var thumbStream = await _files.DownloadAsync(draft.ThumbnailPath, ct);
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                Response.Headers["Content-Disposition"] = $"inline; filename=\"scan-{id}-preview\"";
                return File(thumbStream, "image/jpeg");
            }
            catch
            {
                // Thumbnail unreadable — fall through and serve the original below.
            }
        }

        var storedFile = await _db.StoredFiles
            .FirstOrDefaultAsync(f => f.FilePath == draft.FilePath, ct);

        if (storedFile is null)
            return NotFound(new { error = "File record not found" });

        Stream stream;
        try
        {
            stream = await _files.DownloadAsync(draft.FilePath, ct);
        }
        catch
        {
            return NotFound(new { error = "File not found on storage" });
        }

        // Serve user-uploaded content defensively: never let the browser render active content
        // (html/svg/…) on our own origin. Known-safe types (images + PDF) render inline so the
        // review page preview works; everything else downloads as octet-stream. nosniff always.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (InlineSafeContentTypes.Contains(storedFile.ContentType))
        {
            Response.Headers["Content-Disposition"] = $"inline; filename=\"scan-{id}\"";
            return File(stream, storedFile.ContentType);
        }

        Response.Headers["Content-Disposition"] = $"attachment; filename=\"scan-{id}\"";
        return File(stream, "application/octet-stream");
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/scans/{id}/confirm  — confirm a draft → create Expense
    // -------------------------------------------------------------------------

    [HttpPost("{id:int}/confirm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Confirm(
        int id,
        [FromBody] ConfirmScanRequest? body,
        CancellationToken ct)
    {
        var result = await _scan.ConfirmAndCreateAsync(
            GetPortfolioId(), id, GetUserId(), body?.OverridesJson ?? "{}", ct);

        if (!result.Success)
            return BadRequest(new { error = result.Error });

        // Return a named id field that matches the created entity type so clients can
        // navigate directly to the record. Both keys are included for backward compatibility
        // (older clients that always read expenseId still get a value; newer clients use
        // entityType + entityId for a generic approach).
        return result.EntityType == "Payment"
            ? Ok(new { paymentId = result.CreatedEntityId, entityType = result.EntityType, entityId = result.CreatedEntityId })
            : Ok(new { expenseId = result.CreatedEntityId, entityType = result.EntityType, entityId = result.CreatedEntityId });
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/scans/{id}/reject  — reject a draft
    // -------------------------------------------------------------------------

    [HttpPost("{id:int}/reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reject(
        int id,
        [FromBody] RejectScanRequest? body,
        CancellationToken ct)
    {
        var found = await _scan.RejectDraftAsync(
            GetPortfolioId(), id, GetUserId(), body?.Reason, ct);

        return found ? Ok() : NotFound(new { error = "Scan draft not found" });
    }
}
