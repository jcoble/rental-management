using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
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

    // Recognised scan targets. Empty/null at single-file upload is allowed (the worker auto-classifies);
    // a batch always has a concrete target (defaulting to "Lease", the migration on-ramp).
    private static readonly HashSet<string> ValidTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Expense", "Payment", "WorkOrder", "Lease", "Application"
    };

    // Cap per batch so one request can't enqueue an unbounded number of (paid) LLM extractions.
    private const int MaxBatchFiles = 100;

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
        if (!string.IsNullOrEmpty(targetEntityType) && !ValidTargets.Contains(targetEntityType))
        {
            return BadRequest(new { error = $"targetEntityType '{targetEntityType}' is not valid. Allowed values: Expense, Payment, WorkOrder, Lease, Application (or omit to auto-classify)." });
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
    // POST /api/v1/scans/batch  — bulk upload many documents into one batch
    // -------------------------------------------------------------------------

    /// <summary>
    /// Bulk-scan upload: accepts MANY files in one multipart request (field name <c>files</c>),
    /// creates a <see cref="ScanBatch"/>, validates + stores each file, and creates one Pending
    /// <see cref="ScanDraft"/> per file linked to the batch. The Engine worker then extracts each
    /// draft → Reviewing. Returns the batch + the created draft ids.
    /// </summary>
    [HttpPost("batch")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ScanBatchCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ScanBatchCreatedResponse>> UploadBatch(
        [FromForm] List<IFormFile> files,
        [FromForm] string? targetEntityType,
        [FromForm] string? name,
        CancellationToken ct)
    {
        var nonEmpty = (files ?? []).Where(f => f is { Length: > 0 }).ToList();
        if (nonEmpty.Count == 0)
            return BadRequest(new { error = "At least one non-empty file is required." });

        if (nonEmpty.Count > MaxBatchFiles)
            return BadRequest(new { error = $"A batch can contain at most {MaxBatchFiles} files (got {nonEmpty.Count})." });

        // A batch always targets a concrete entity; default to the lease-import on-ramp.
        var target = string.IsNullOrWhiteSpace(targetEntityType) ? "Lease" : targetEntityType.Trim();
        if (!ValidTargets.Contains(target))
            return BadRequest(new { error = $"targetEntityType '{target}' is not valid. Allowed values: Expense, Payment, WorkOrder, Lease." });

        // Normalize to the canonical casing so the worker's case-sensitive target checks match.
        target = ValidTargets.First(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));

        var portfolioId = GetPortfolioId();

        // Read all file bytes up front so a per-file validation failure rejects the whole batch
        // before any draft is created (all-or-nothing intake — no half-imported batch to clean up).
        var payloads = new List<(byte[] Bytes, string ContentType)>(nonEmpty.Count);
        foreach (var file in nonEmpty)
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            payloads.Add((ms.ToArray(), file.ContentType));
        }

        var batch = new ScanBatch
        {
            PortfolioId = portfolioId,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            TargetEntityType = target,
            Status = ScanBatchStatus.Processing,
            FileCount = payloads.Count,
            CreatedAtUtc = DateTime.UtcNow,
        };
        _db.ScanBatches.Add(batch);
        await _db.SaveChangesAsync(ct);

        var draftIds = new List<int>(payloads.Count);
        try
        {
            foreach (var (bytes, contentType) in payloads)
            {
                var draft = await _scan.CreateBatchDraftAsync(portfolioId, batch.Id, bytes, contentType, target, ct);
                draftIds.Add(draft.Id);
            }
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        return CreatedAtAction(
            nameof(GetBatch),
            new { id = batch.Id },
            new ScanBatchCreatedResponse(
                batch.Id, batch.Name, batch.TargetEntityType, batch.Status.ToString(), batch.FileCount, draftIds));
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/scans/batches  — list batches with rollup counts
    // -------------------------------------------------------------------------

    [HttpGet("batches")]
    [ProducesResponseType(typeof(IReadOnlyList<ScanBatchSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ScanBatchSummaryResponse>>> ListBatches(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var portfolioId = GetPortfolioId();

        var batches = await _db.ScanBatches
            .Where(b => b.PortfolioId == portfolioId)
            .OrderByDescending(b => b.CreatedAtUtc)
            .ThenByDescending(b => b.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        if (batches.Count == 0)
            return Ok(Array.Empty<ScanBatchSummaryResponse>());

        var batchIds = batches.Select(b => b.Id).ToList();

        // One grouped query for all the rollup counts (status per batch) instead of N per-batch queries.
        var statusCounts = await _db.ScanDrafts
            .Where(d => d.PortfolioId == portfolioId && d.BatchId != null && batchIds.Contains(d.BatchId.Value))
            .GroupBy(d => new { BatchId = d.BatchId!.Value, d.Status })
            .Select(g => new { g.Key.BatchId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var countsByBatch = statusCounts
            .GroupBy(x => x.BatchId)
            .ToDictionary(g => g.Key, g => BuildCounts(g.Select(x => (x.Status, x.Count))));

        var result = batches.Select(b =>
        {
            var counts = countsByBatch.TryGetValue(b.Id, out var c) ? c : EmptyCounts;
            return new ScanBatchSummaryResponse(
                b.Id, b.Name, b.TargetEntityType, ComputeStatus(b, counts).ToString(),
                b.FileCount, b.CreatedAtUtc, counts);
        }).ToList();

        return Ok(result);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/scans/batches/{id}  — batch + its drafts (the review queue)
    // -------------------------------------------------------------------------

    [HttpGet("batches/{id:int}", Name = nameof(GetBatch))]
    [ProducesResponseType(typeof(ScanBatchDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScanBatchDetailResponse>> GetBatch(int id, CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();

        var batch = await _db.ScanBatches
            .FirstOrDefaultAsync(b => b.Id == id && b.PortfolioId == portfolioId, ct);

        if (batch is null)
            return NotFound(new { error = "Scan batch not found" });

        // Portfolio-scoped: only this portfolio's drafts in this batch (IDOR-safe — a foreign caller
        // can neither read the batch above nor any draft here).
        var drafts = await _db.ScanDrafts
            .Where(d => d.PortfolioId == portfolioId && d.BatchId == id)
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .ToListAsync(ct);

        var counts = BuildCounts(drafts.Select(d => (d.Status, 1)));

        // Resolve the created-entity id for any confirmed draft so the UI can link straight to the record.
        var filePaths = drafts.Select(d => d.FilePath).ToHashSet(StringComparer.Ordinal);
        var linkedFiles = await _db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == portfolioId && filePaths.Contains(f.FilePath))
            .ToDictionaryAsync(f => f.FilePath, ct);

        var draftDtos = drafts.Select(d =>
        {
            linkedFiles.TryGetValue(d.FilePath, out var linkedFile);
            var (tenant, unit, term) = SummarizeLeaseFields(d.ExtractedFields);
            return new ScanBatchDraftResponse(
                d.Id, d.Status, d.TargetEntityType, $"/api/v1/scans/{d.Id}/file",
                tenant, unit, term,
                d.Status == "Confirmed" ? linkedFile?.EntityId : null,
                d.CreatedAt);
        }).ToList();

        return Ok(new ScanBatchDetailResponse(
            batch.Id, batch.Name, batch.TargetEntityType, ComputeStatus(batch, counts).ToString(),
            batch.FileCount, batch.CreatedAtUtc, counts, draftDtos));
    }

    // -------------------------------------------------------------------------
    // Batch rollup helpers
    // -------------------------------------------------------------------------

    private static readonly ScanBatchCounts EmptyCounts = new(0, 0, 0, 0, 0, 0);

    /// <summary>Folds per-status draft counts into a <see cref="ScanBatchCounts"/> rollup.</summary>
    private static ScanBatchCounts BuildCounts(IEnumerable<(string Status, int Count)> statusCounts)
    {
        int total = 0, pending = 0, reviewing = 0, confirmed = 0, rejected = 0, failed = 0;
        foreach (var (status, count) in statusCounts)
        {
            total += count;
            switch (status)
            {
                // "Processing" (mid-extraction) and "Confirming" (mid-confirm) are transient; surface
                // them under Pending so a batch still in flight reads as not-yet-reviewable.
                case "Pending" or "Processing" or "Confirming": pending += count; break;
                case "Reviewing": reviewing += count; break;
                case "Confirmed": confirmed += count; break;
                case "Rejected": rejected += count; break;
                case "Failed": failed += count; break;
            }
        }
        return new ScanBatchCounts(total, pending, reviewing, confirmed, rejected, failed);
    }

    /// <summary>
    /// Computes the batch's effective status from its draft rollup (cheap on read so a confirm/reject
    /// never has to touch the batch row): Completed when every draft is confirmed/rejected, Reviewing
    /// when at least one draft is ready to review, otherwise still Processing.
    /// </summary>
    private static ScanBatchStatus ComputeStatus(ScanBatch batch, ScanBatchCounts counts)
    {
        if (counts.Total > 0 && counts.Pending == 0 && counts.Reviewing == 0)
            return ScanBatchStatus.Completed;
        if (counts.Reviewing > 0 || counts.Confirmed > 0 || counts.Rejected > 0)
            return ScanBatchStatus.Reviewing;
        return ScanBatchStatus.Processing;
    }

    /// <summary>
    /// Pulls a short, human-friendly tenant / unit / term summary out of a lease draft's extracted-field
    /// JSON (<c>{field:{value,confidence}}</c>) for the review queue. Returns nulls for non-lease drafts
    /// or any field that isn't present; never throws on malformed JSON.
    /// </summary>
    private static (string? Tenant, string? Unit, string? Term) SummarizeLeaseFields(string? extractedFieldsJson)
    {
        if (string.IsNullOrWhiteSpace(extractedFieldsJson))
            return (null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(extractedFieldsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            var tenant = ReadValue(root, "tenant_name") ?? ReadValue(root, "tenantName");
            var unit = ReadValue(root, "unit_id") ?? ReadValue(root, "unitId");
            var start = ReadValue(root, "start_date") ?? ReadValue(root, "startDate");
            var end = ReadValue(root, "end_date") ?? ReadValue(root, "endDate");

            string? term = (start, end) switch
            {
                ({ } s, { } e) => $"{s} – {e}",
                ({ } s, null) => s,
                (null, { } e) => e,
                _ => null,
            };

            return (tenant, string.IsNullOrWhiteSpace(unit) ? null : unit, term);
        }
        catch
        {
            return (null, null, null);
        }
    }

    /// <summary>Reads the <c>value</c> string from a <c>{key:{value,confidence}}</c> field; null if absent.</summary>
    private static string? ReadValue(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var fieldEl))
            return null;
        if (fieldEl.ValueKind == JsonValueKind.Object &&
            fieldEl.TryGetProperty("value", out var valueEl) &&
            valueEl.ValueKind == JsonValueKind.String)
        {
            var s = valueEl.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        if (fieldEl.ValueKind == JsonValueKind.String)
        {
            var s = fieldEl.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/scans/{id}  — fetch a single draft
    // -------------------------------------------------------------------------

    [HttpGet("{id:int}", Name = nameof(Get))]
    [ProducesResponseType(typeof(ScanDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScanDraftResponse>> Get(int id, CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();
        var draft = await _db.ScanDrafts
            .FirstOrDefaultAsync(d => d.Id == id && d.PortfolioId == portfolioId, ct);

        if (draft is null)
            return NotFound(new { error = "Scan draft not found" });

        var linkedFile = await _db.StoredFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.PortfolioId == portfolioId && f.FilePath == draft.FilePath, ct);

        var response = ScanDraftResponse.FromEntity(draft, linkedFile?.EntityType, linkedFile?.EntityId);

        // For a lease draft, attach the property/unit import proposal (link-existing vs create-new) so the
        // review UI can show what confirming will do — the empty-portfolio bootstrap is visible up front.
        // Uses no overrides: this is the default preview before the reviewer edits anything.
        if (draft.TargetEntityType is "Lease")
        {
            var proposal = await _scan.BuildLeaseProposalAsync(portfolioId, id, overridesJson: "{}", ct);
            response = response.WithLeaseProposal(proposal);
        }

        return Ok(response);
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

        var filePaths = drafts.Select(d => d.FilePath).ToHashSet(StringComparer.Ordinal);
        var linkedFiles = await _db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == portfolioId && filePaths.Contains(f.FilePath))
            .ToDictionaryAsync(f => f.FilePath, ct);

        return Ok(drafts.Select(d =>
        {
            linkedFiles.TryGetValue(d.FilePath, out var linkedFile);
            return ScanDraftResponse.FromEntity(d, linkedFile?.EntityType, linkedFile?.EntityId);
        }).ToList());
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
        return result.EntityType switch
        {
            "Payment" => Ok(new { paymentId = result.CreatedEntityId, entityType = result.EntityType, entityId = result.CreatedEntityId }),
            "WorkOrder" => Ok(new { workOrderId = result.CreatedEntityId, entityType = result.EntityType, entityId = result.CreatedEntityId }),
            "Lease" => Ok(new { leaseId = result.CreatedEntityId, entityType = result.EntityType, entityId = result.CreatedEntityId }),
            "Application" => Ok(new { applicationId = result.CreatedEntityId, entityType = result.EntityType, entityId = result.CreatedEntityId }),
            _ => Ok(new { expenseId = result.CreatedEntityId, entityType = result.EntityType, entityId = result.CreatedEntityId }),
        };
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
