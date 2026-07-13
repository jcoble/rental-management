using System.Linq.Expressions;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Upload-and-scan intake endpoints. All routes are scoped to the caller's portfolio
/// via the JWT <c>portfolioId</c> claim — no <c>{portfolioId}</c> route parameter.
/// </summary>
[ApiController]
[Route("api/v1/scans")]
[Produces("application/json")]
public class ScanController : ManagementControllerBase
{
    private static readonly AtomicJsonResultCodec<ConfirmScanDraftResult> ConfirmResultCodec =
        new("scan-confirm.result.v1");

    private readonly IScanService _scan;
    private readonly IScanUploadService _uploads;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    // Content types we trust to render inline (non-active: no script execution). Anything else
    // is forced to download as octet-stream so an uploaded html/svg/etc. can't run on our origin.
    private static readonly HashSet<string> InlineSafeContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/png", "image/gif", "image/webp", "image/heic"
    };

    // Recognised scan targets. Empty/null at single-file upload is allowed (the worker auto-classifies);
    // a batch always has a concrete target (defaulting to the canonical Agreement import on-ramp).
    private static readonly HashSet<string> ValidTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Expense", "Payment", "WorkOrder", nameof(LeaseAgreement), "Application", "Loan"
    };

    // Cap per batch so one request can't enqueue an unbounded number of (paid) LLM extractions.
    private const int MaxBatchFiles = 100;

    public ScanController(
        IScanService scan,
        IScanUploadService uploads,
        IAtomicUnitOfWork atomic,
        RentalCommandDbContext db,
        IFileStorage files)
    {
        _scan = scan;
        _uploads = uploads;
        _atomic = atomic;
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
        [FromForm] string clientOperationId,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty file is required." });
        if (string.IsNullOrWhiteSpace(clientOperationId) || clientOperationId.Length > 160)
            return BadRequest(new { error = "A non-blank clientOperationId of at most 160 characters is required." });

        // When a targetEntityType is explicitly provided it must be a recognised value.
        // Empty/null is allowed — the LLM worker will classify it during processing.
        if (!string.IsNullOrEmpty(targetEntityType) && !ValidTargets.Contains(targetEntityType))
        {
            return BadRequest(new { error = $"targetEntityType '{targetEntityType}' is not valid. Allowed values: Expense, Payment, WorkOrder, LeaseAgreement, Application, Loan (or omit to auto-classify)." });
        }
        if (!string.IsNullOrEmpty(targetEntityType))
            targetEntityType = ValidTargets.First(target =>
                string.Equals(target, targetEntityType, StringComparison.OrdinalIgnoreCase));

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        try
        {
            var result = await _uploads.UploadAsync(
                GetPortfolioId(),
                GetUserId(),
                clientOperationId,
                targetEntityType,
                createBatch: false,
                batchName: null,
                await BuildCaptureContextAsync(ct),
                [new ScanUploadFilePayload(bytes, file.FileName, file.ContentType)],
                ct);
            var draft = result.Drafts.Single();

            return CreatedAtAction(
                nameof(Get),
                new { id = draft.DraftId },
                new ScanCreatedResponse(draft.DraftId, draft.Status, $"/api/v1/scans/{draft.DraftId}/file"));
        }
        catch (UploadOperationConflictException ex)
        {
            return Conflict(new { error = ex.Message });
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
        [FromForm] string clientOperationId,
        CancellationToken ct)
    {
        var nonEmpty = (files ?? []).Where(f => f is { Length: > 0 }).ToList();
        if (nonEmpty.Count == 0)
            return BadRequest(new { error = "At least one non-empty file is required." });
        if (string.IsNullOrWhiteSpace(clientOperationId) || clientOperationId.Length > 160)
            return BadRequest(new { error = "A non-blank clientOperationId of at most 160 characters is required." });

        if (nonEmpty.Count > MaxBatchFiles)
            return BadRequest(new { error = $"A batch can contain at most {MaxBatchFiles} files (got {nonEmpty.Count})." });

        // A batch always targets a concrete entity; default to the lease-import on-ramp.
        var target = string.IsNullOrWhiteSpace(targetEntityType) ? nameof(LeaseAgreement) : targetEntityType.Trim();
        if (!ValidTargets.Contains(target))
            return BadRequest(new { error = $"targetEntityType '{target}' is not valid. Allowed values: Expense, Payment, WorkOrder, LeaseAgreement, Application, Loan." });

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

        try
        {
            var result = await _uploads.UploadAsync(
                portfolioId,
                GetUserId(),
                clientOperationId,
                target,
                createBatch: true,
                name,
                await BuildCaptureContextAsync(ct),
                payloads.Select((payload, index) => new ScanUploadFilePayload(
                    payload.Bytes,
                    nonEmpty[index].FileName,
                    payload.ContentType)).ToArray(),
                ct);
            var batchId = result.BatchId
                ?? throw new InvalidOperationException("Atomic batch upload returned no batch id.");

            return CreatedAtAction(
                nameof(GetBatch),
                new { id = batchId },
                new ScanBatchCreatedResponse(
                    batchId,
                    result.BatchName,
                    result.TargetEntityType,
                    nameof(ScanBatchStatus.Processing),
                    result.Drafts.Count,
                    result.Drafts.Select(draft => draft.DraftId).ToArray()));
        }
        catch (UploadOperationConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task<ScanCaptureContextData> BuildCaptureContextAsync(CancellationToken ct)
    {
        var active = HttpContext.Items.TryGetValue(CanonicalAccessContextHttpItem.Key, out var value)
            ? value as ActiveAccessContext
            : null;
        IFormCollection? form = null;
        if (Request.HasFormContentType)
            form = await Request.ReadFormAsync(ct);

        static int? Positive(IFormCollection? values, string key) =>
            values is not null && int.TryParse(values[key], out var parsed) && parsed > 0
                ? parsed
                : null;
        static long? PositiveLong(IFormCollection? values, string key) =>
            values is not null && long.TryParse(values[key], out var parsed) && parsed > 0
                ? parsed
                : null;
        static string? Text(IFormCollection? values, string key, int maxLength)
        {
            var text = values?[key].ToString().Trim();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : text.Length <= maxLength
                    ? text
                    : throw new ArgumentException($"{key} cannot exceed {maxLength} characters.");
        }

        return new ScanCaptureContextData(
            active?.LastAuthorizedExperience ?? active?.DefaultExperience,
            active?.AccessContextId,
            active?.AccessRevision,
            Positive(form, "propertyId"),
            Positive(form, "unitId"),
            Positive(form, "leaseManagementId"),
            Positive(form, "tenantAccountId"),
            Text(form, "focusedRecordKind", 80),
            PositiveLong(form, "focusedRecordId"),
            Text(form, "sourceLabel", 100));
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
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);

        var batches = await QueryBatchSummaryRows(portfolioId)
            .OrderByDescending(b => b.CreatedAtUtc)
            .ThenByDescending(b => b.Id)
            .Skip(skip)
            .Take(take)
            .Select(BatchSummaryProjection)
            .ToListAsync(ct);

        return Ok(batches);
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

        var batch = await QueryBatchSummaryRows(portfolioId)
            .Where(b => b.Id == id)
            .Select(BatchSummaryProjection)
            .SingleOrDefaultAsync(ct);

        if (batch is null)
            return NotFound(new { error = "Scan batch not found" });

        // Portfolio-scoped: only this portfolio's drafts in this batch (IDOR-safe — a foreign caller
        // can neither read the batch above nor any draft here).
        var drafts = await _db.ScanDrafts
            .AsNoTracking()
            .Where(d => d.PortfolioId == portfolioId && d.BatchId == id)
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .Select(d => new ScanBatchDraftQueryRow
            {
                Id = d.Id,
                Status = d.Status,
                TargetEntityType = d.TargetEntityType,
                ExtractedFields = d.ExtractedFields,
                ConfirmedEntityId = d.Status == "Confirmed" ? d.ConfirmedEntityId : null,
                CreatedAt = d.CreatedAt,
                FailureReason = d.FailureReason,
            })
            .ToListAsync(ct);

        var draftDtos = new List<ScanBatchDraftResponse>(drafts.Count);
        foreach (var draft in drafts)
        {
            var (tenant, unit, term) = SummarizeLeaseFields(draft.ExtractedFields);
            draftDtos.Add(new ScanBatchDraftResponse(
                draft.Id, draft.Status, draft.TargetEntityType, $"/api/v1/scans/{draft.Id}/file",
                tenant, unit, term,
                draft.ConfirmedEntityId,
                draft.CreatedAt,
                draft.FailureReason));
        }

        return Ok(new ScanBatchDetailResponse(
            batch.Id, batch.Name, batch.TargetEntityType, batch.Status,
            batch.FileCount, batch.CreatedAtUtc, batch.Counts, draftDtos));
    }

    // -------------------------------------------------------------------------
    // Batch rollup helpers
    // -------------------------------------------------------------------------

    private IQueryable<ScanBatchSummaryQueryRow> QueryBatchSummaryRows(int portfolioId) =>
        _db.ScanBatches
            .AsNoTracking()
            .Where(batch => batch.PortfolioId == portfolioId)
            .Select(batch => new ScanBatchSummaryQueryRow
            {
                Id = batch.Id,
                Name = batch.Name,
                TargetEntityType = batch.TargetEntityType,
                FileCount = batch.FileCount,
                CreatedAtUtc = batch.CreatedAtUtc,
                Total = _db.ScanDrafts.Count(d => d.PortfolioId == portfolioId && d.BatchId == batch.Id),
                Pending = _db.ScanDrafts.Count(d =>
                    d.PortfolioId == portfolioId && d.BatchId == batch.Id &&
                    (d.Status == "Pending" || d.Status == "Processing" || d.Status == "Confirming")),
                Reviewing = _db.ScanDrafts.Count(d =>
                    d.PortfolioId == portfolioId && d.BatchId == batch.Id && d.Status == "Reviewing"),
                Confirmed = _db.ScanDrafts.Count(d =>
                    d.PortfolioId == portfolioId && d.BatchId == batch.Id && d.Status == "Confirmed"),
                Rejected = _db.ScanDrafts.Count(d =>
                    d.PortfolioId == portfolioId && d.BatchId == batch.Id && d.Status == "Rejected"),
                Failed = _db.ScanDrafts.Count(d =>
                    d.PortfolioId == portfolioId && d.BatchId == batch.Id && d.Status == "Failed"),
            });

    private static readonly Expression<Func<ScanBatchSummaryQueryRow, ScanBatchSummaryResponse>> BatchSummaryProjection =
        row => new ScanBatchSummaryResponse(
            row.Id,
            row.Name,
            row.TargetEntityType,
            row.Total > 0 && row.Pending == 0 && row.Reviewing == 0
                ? "Completed"
                : row.Reviewing > 0 || row.Confirmed > 0 || row.Rejected > 0
                    ? "Reviewing"
                    : "Processing",
            row.FileCount,
            row.CreatedAtUtc,
            new ScanBatchCounts(row.Total, row.Pending, row.Reviewing, row.Confirmed, row.Rejected, row.Failed));

    private sealed class ScanBatchSummaryQueryRow
    {
        public int Id { get; init; }
        public string? Name { get; init; }
        public string TargetEntityType { get; init; } = string.Empty;
        public int FileCount { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public int Total { get; init; }
        public int Pending { get; init; }
        public int Reviewing { get; init; }
        public int Confirmed { get; init; }
        public int Rejected { get; init; }
        public int Failed { get; init; }
    }

    private sealed class ScanBatchDraftQueryRow
    {
        public int Id { get; init; }
        public string Status { get; init; } = string.Empty;
        public string TargetEntityType { get; init; } = string.Empty;
        public string? ExtractedFields { get; init; }
        public int? ConfirmedEntityId { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? FailureReason { get; init; }
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
            var unit = ReadValue(root, "unit_number")
                ?? ReadValue(root, "unitNumber")
                ?? ReadValue(root, "unit_id")
                ?? ReadValue(root, "unitId");
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

        ScanDraftResponse response;
        if (draft.Status == "Confirmed"
            && draft.TargetEntityType == "Payment"
            && draft.ConfirmedEntityId is int tenantAccountId)
        {
            var receipt = await (
                from entry in _db.TenantLedgerEntries.AsNoTracking()
                join account in _db.TenantAccounts.AsNoTracking()
                    on new { entry.PortfolioId, entry.TenantAccountId }
                    equals new { account.PortfolioId, TenantAccountId = account.Id }
                where entry.PortfolioId == portfolioId
                    && entry.TenantAccountId == tenantAccountId
                    && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && entry.BusinessKey == "scan-receipt:" + draft.Id
                select new
                {
                    EntryId = (long?)entry.Id,
                    UnitId = (int?)account.LeaseManagement!.UnitId,
                }).SingleOrDefaultAsync(ct);
            response = ScanDraftResponse.FromEntity(
                draft, "Payment", receipt?.EntryId, receipt?.UnitId);
        }
        else
        {
            var linkedFile = draft.SourceStoredFileId is int sourceStoredFileId
                ? await _db.StoredFiles.AsNoTracking().SingleOrDefaultAsync(
                    file => file.Id == sourceStoredFileId && file.PortfolioId == portfolioId, ct)
                : null;
            var createdUnitId = await ResolveCreatedUnitIdAsync(
                portfolioId, linkedFile?.EntityType, linkedFile?.EntityId, ct);
            response = ScanDraftResponse.FromEntity(
                draft, linkedFile?.EntityType, linkedFile?.EntityId, createdUnitId);
        }

        // For a lease draft, attach the property/unit import proposal (link-existing vs create-new) so the
        // review UI can show what confirming will do — the empty-portfolio bootstrap is visible up front.
        // Uses no overrides: this is the default preview before the reviewer edits anything.
        if (draft.TargetEntityType is nameof(LeaseAgreement))
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
        var page = await LoadScanDraftPageAsync(
            GetPortfolioId(),
            status,
            new ListQuery { Skip = skip, Take = take },
            ct);
        return Ok(page.Items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(ScanDraftListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ScanDraftListResponse>> ListPage(
        [FromQuery] ListQuery query,
        [FromQuery] string? status,
        CancellationToken ct = default)
    {
        var page = await LoadScanDraftPageAsync(GetPortfolioId(), status, query, ct);
        return Ok(page);
    }

    private async Task<ScanDraftListResponse> LoadScanDraftPageAsync(
        int portfolioId,
        string? status,
        ListQuery listQuery,
        CancellationToken ct)
    {
        var query = _db.ScanDrafts
            .AsNoTracking()
            .Where(d => d.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(d => d.Status == status);

        query = listQuery.SortField switch
        {
            "status" => listQuery.SortDescending ? query.OrderByDescending(d => d.Status) : query.OrderBy(d => d.Status),
            "targetentitytype" => listQuery.SortDescending ? query.OrderByDescending(d => d.TargetEntityType) : query.OrderBy(d => d.TargetEntityType),
            "createdat" => listQuery.SortDescending ? query.OrderByDescending(d => d.CreatedAt) : query.OrderBy(d => d.CreatedAt),
            _ => query.OrderByDescending(d => d.CreatedAt),
        };

        var totalCount = await query.CountAsync(ct);

        var drafts = await query
            .Skip(listQuery.NormalizedSkip)
            .Take(listQuery.NormalizedTake)
            .Select(d => new ScanDraftPageQueryRow
            {
                Id = d.Id,
                PortfolioId = d.PortfolioId,
                TargetEntityType = d.TargetEntityType,
                Status = d.Status,
                ExtractedFields = d.ExtractedFields,
                ModelId = d.ModelId,
                TokensUsed = d.TokensUsed,
                CostUsd = d.CostUsd,
                FailureReason = d.FailureReason,
                CreatedAt = d.CreatedAt,
                ReviewedAt = d.ReviewedAt,
                ConfirmedAt = d.ConfirmedAt,
                SourceContentSha256 = d.SourceContentSha256,
                SourceLabel = d.SourceLabel,
                CaptureExperience = d.CaptureExperience,
                CaptureAccessContextId = d.CaptureAccessContextId,
                CaptureAccessRevision = d.CaptureAccessRevision,
                CapturePropertyId = d.CapturePropertyId,
                CaptureUnitId = d.CaptureUnitId,
                CaptureLeaseManagementId = d.CaptureLeaseManagementId,
                CaptureTenantAccountId = d.CaptureTenantAccountId,
                CaptureFocusedRecordKind = d.CaptureFocusedRecordKind,
                CaptureFocusedRecordId = d.CaptureFocusedRecordId,
                CreatedEntityType = d.Status == "Confirmed" && d.ConfirmedEntityId != null
                    ? d.TargetEntityType
                    : null,
                CreatedEntityId = d.Status != "Confirmed" || d.ConfirmedEntityId == null
                    ? null
                    : d.TargetEntityType == "Payment"
                        ? _db.TenantLedgerEntries
                            .Where(entry => entry.PortfolioId == portfolioId
                                && entry.TenantAccountId == d.ConfirmedEntityId
                                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                                && entry.BusinessKey == "scan-receipt:" + d.Id)
                            .Select(entry => (long?)entry.Id)
                            .FirstOrDefault()
                        : (long?)d.ConfirmedEntityId,
                CreatedUnitId = d.Status != "Confirmed" || d.ConfirmedEntityId == null
                    ? null
                    : d.TargetEntityType == "Payment"
                        ? _db.TenantAccounts
                            .Where(account => account.PortfolioId == portfolioId
                                && account.Id == d.ConfirmedEntityId)
                            .Select(account => account.LeaseManagement != null
                                ? (int?)account.LeaseManagement.UnitId
                                : null)
                            .FirstOrDefault()
                        : d.TargetEntityType == "Expense"
                            ? _db.Expenses
                                .Where(e => e.PortfolioId == portfolioId && e.Id == d.ConfirmedEntityId)
                                .Select(e => e.UnitId ?? (e.WorkOrder != null ? e.WorkOrder.UnitId : null))
                                .FirstOrDefault()
                            : d.TargetEntityType == "WorkOrder"
                                ? _db.WorkOrders
                                    .Where(w => w.PortfolioId == portfolioId && w.Id == d.ConfirmedEntityId)
                                    .Select(w => w.UnitId)
                                    .FirstOrDefault()
                                : d.TargetEntityType == nameof(LeaseAgreement)
                                    ? _db.LeaseAgreements
                                        .Where(agreement => agreement.PortfolioId == portfolioId
                                            && agreement.Id == d.ConfirmedEntityId)
                                        .Select(agreement => (int?)agreement.LeaseManagement!.UnitId)
                                        .FirstOrDefault()
                                    : d.TargetEntityType == "Application" || d.TargetEntityType == "RentalApplication"
                                        ? _db.RentalApplications
                                            .Where(a => a.PortfolioId == portfolioId && a.Id == d.ConfirmedEntityId)
                                            .Select(a => a.UnitId)
                                            .FirstOrDefault()
                                        : null,
            })
            .ToListAsync(ct);

        var items = new List<ScanDraftResponse>(drafts.Count);
        foreach (var draft in drafts)
        {
            items.Add(new ScanDraftResponse(
                draft.Id, draft.PortfolioId, draft.TargetEntityType, draft.Status,
                $"/api/v1/scans/{draft.Id}/file",
                ScanDraftResponse.ParseFields(draft.ExtractedFields),
                draft.ModelId, draft.TokensUsed, draft.CostUsd, draft.FailureReason,
                draft.CreatedAt, draft.ReviewedAt, draft.ConfirmedAt,
                draft.CreatedEntityType, draft.CreatedEntityId, draft.CreatedUnitId,
                CaptureContext: new ScanCaptureContextDto(
                    draft.CaptureExperience?.ToString(), draft.CaptureAccessContextId,
                    draft.CaptureAccessRevision, draft.CapturePropertyId, draft.CaptureUnitId,
                    draft.CaptureLeaseManagementId, draft.CaptureTenantAccountId,
                    draft.CaptureFocusedRecordKind, draft.CaptureFocusedRecordId, draft.SourceLabel),
                SourceContentSha256: draft.SourceContentSha256));
        }

        return new ScanDraftListResponse(items, totalCount, listQuery.NormalizedSkip, listQuery.NormalizedTake);
    }

    private async Task<int?> ResolveCreatedUnitIdAsync(
        int portfolioId,
        string? entityType,
        int? entityId,
        CancellationToken ct)
    {
        if (entityId is not > 0 || string.IsNullOrWhiteSpace(entityType))
        {
            return null;
        }

        return entityType switch
        {
            "Expense" => await _db.Expenses
                .AsNoTracking()
                .Where(e => e.PortfolioId == portfolioId && e.Id == entityId.Value)
                .Select(e => e.UnitId ?? (e.WorkOrder != null ? e.WorkOrder.UnitId : null))
                .FirstOrDefaultAsync(ct),
            "WorkOrder" => await _db.WorkOrders
                .AsNoTracking()
                .Where(w => w.PortfolioId == portfolioId && w.Id == entityId.Value)
                .Select(w => w.UnitId)
                .FirstOrDefaultAsync(ct),
            nameof(LeaseAgreement) => await _db.LeaseAgreements
                .AsNoTracking()
                .Where(agreement => agreement.PortfolioId == portfolioId && agreement.Id == entityId.Value)
                .Select(agreement => (int?)agreement.LeaseManagement!.UnitId)
                .FirstOrDefaultAsync(ct),
            "Application" or "RentalApplication" => await _db.RentalApplications
                .AsNoTracking()
                .Where(a => a.PortfolioId == portfolioId && a.Id == entityId.Value)
                .Select(a => a.UnitId)
                .FirstOrDefaultAsync(ct),
            _ => null,
        };
    }

    private sealed class ScanDraftPageQueryRow
    {
        public int Id { get; init; }
        public int PortfolioId { get; init; }
        public string TargetEntityType { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? ExtractedFields { get; init; }
        public string? ModelId { get; init; }
        public int? TokensUsed { get; init; }
        public decimal? CostUsd { get; init; }
        public string? FailureReason { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? ReviewedAt { get; init; }
        public DateTime? ConfirmedAt { get; init; }
        public string? SourceContentSha256 { get; init; }
        public string? SourceLabel { get; init; }
        public WorkspaceExperience? CaptureExperience { get; init; }
        public int? CaptureAccessContextId { get; init; }
        public long? CaptureAccessRevision { get; init; }
        public int? CapturePropertyId { get; init; }
        public int? CaptureUnitId { get; init; }
        public int? CaptureLeaseManagementId { get; init; }
        public int? CaptureTenantAccountId { get; init; }
        public string? CaptureFocusedRecordKind { get; init; }
        public long? CaptureFocusedRecordId { get; init; }
        public string? CreatedEntityType { get; init; }
        public long? CreatedEntityId { get; init; }
        public int? CreatedUnitId { get; init; }
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/scans/{id}/retry  — requeue a failed draft for extraction
    // -------------------------------------------------------------------------

    [HttpPost("{id:int}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retry(int id, CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();

        var updated = await _db.ScanDrafts
            .Where(d => d.Id == id && d.PortfolioId == portfolioId && d.Status == "Failed")
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, "Pending")
                .SetProperty(d => d.ExtractedFields, (string?)null)
                .SetProperty(d => d.ModelId, (string?)null)
                .SetProperty(d => d.TokensUsed, (int?)null)
                .SetProperty(d => d.CostUsd, (decimal?)null)
                .SetProperty(d => d.FailureReason, (string?)null)
                .SetProperty(d => d.ReviewedAt, (DateTime?)null)
                .SetProperty(d => d.ReviewedBy, (string?)null)
                .SetProperty(d => d.ConfirmedAt, (DateTime?)null), ct);

        if (updated == 1)
            return Ok();

        var exists = await _db.ScanDrafts
            .AnyAsync(d => d.Id == id && d.PortfolioId == portfolioId, ct);

        return exists
            ? BadRequest(new { error = "Only failed scan drafts can be retried." })
            : NotFound(new { error = "Scan draft not found" });
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/scans/{id}/file  — stream the stored file
    // -------------------------------------------------------------------------

    [HttpGet("{id:int}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadFile(int id, [FromQuery] string? full, CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();

        // Accept both ?full=1 and ?full=true (case-insensitive). A plain bool param would
        // 400 on "1"/"0", so parse the flag ourselves; anything else (null, empty, "0",
        // "false", junk) means "serve the thumbnail" rather than erroring.
        var wantsFull = full is not null
            && (full.Equals("1", StringComparison.OrdinalIgnoreCase)
                || full.Equals("true", StringComparison.OrdinalIgnoreCase));

        var draft = await _db.ScanDrafts
            .FirstOrDefaultAsync(d => d.Id == id && d.PortfolioId == portfolioId, ct);

        if (draft is null)
            return NotFound(new { error = "Scan draft not found" });

        // Serve the small JPEG preview by default so clients (esp. phones) don't pull the
        // full-resolution original. The original is available on ?full=1 or ?full=true. Fall
        // back to the original if the thumbnail is missing/unreadable (older scans, PDFs, etc.).
        if (!wantsFull && !string.IsNullOrEmpty(draft.ThumbnailPath))
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
            .FirstOrDefaultAsync(f => f.FilePath == draft.FilePath && f.PortfolioId == portfolioId, ct);

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
    // POST /api/v1/scans/{id}/confirm  — atomically confirm a reviewed draft
    // -------------------------------------------------------------------------

    [HttpPost("{id:int}/confirm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Confirm(
        int id,
        [FromBody] ConfirmScanRequest? body,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.ClientOperationId)
            || body.ClientOperationId.Trim().Length > 160)
        {
            return BadRequest(new { error = "clientOperationId is required and cannot exceed 160 characters." });
        }

        var portfolioId = GetPortfolioId();
        ScanConfirmationPreparation preparation;
        try
        {
            preparation = await _scan.PrepareConfirmationAsync(
                portfolioId, id, GetUserId(), body.OverridesJson ?? "{}", ct);
        }
        catch (ScanConfirmationValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        if (preparation.Outcome == ScanConfirmationPreparationOutcome.DraftNotFound)
            return NotFound(new { error = preparation.Error });
        if (preparation.Outcome == ScanConfirmationPreparationOutcome.TemporarilyUnavailable)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = preparation.Error });
        if (preparation.Outcome != ScanConfirmationPreparationOutcome.Ready
            || preparation.Command is null)
            return BadRequest(new { error = preparation.Error ?? "Scan confirmation request is invalid." });

        if (!Guid.TryParse(User.FindFirstValue("sid"), out var authSessionId)
            || !int.TryParse(User.FindFirstValue("ctx"), out var accessContextId)
            || !long.TryParse(User.FindFirstValue("ar"), out var accessRevision))
            return Forbid();
        var operationDigest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(body.ClientOperationId.Trim())))
            .ToLowerInvariant();
        var command = preparation.Command with
        {
            AuthSessionId = authSessionId,
            AccessContextId = accessContextId,
            ExpectedAccessRevision = accessRevision,
            DeliveryIdempotencyKey = $"scan-confirm:{portfolioId}:{id}:{operationDigest}",
        };

        AtomicCommandOutcome<ConfirmScanDraftResult> atomicResult;
        try
        {
            atomicResult = await _atomic.ExecuteAsync(
                ScanConfirmationCommandIdentity.Create(
                    portfolioId, id, body.ClientOperationId),
                command,
                ConfirmResultCodec,
                ct);
        }
        catch (ScanConfirmationValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }

        var result = atomicResult.Value;
        return result.Outcome switch
        {
            ConfirmScanDraftOutcome.Confirmed or ConfirmScanDraftOutcome.AlreadyConfirmed =>
                ConfirmationOk(result, atomicResult.Disposition),
            ConfirmScanDraftOutcome.DraftNotFound => NotFound(new { error = result.Error ?? "Scan draft not found." }),
            ConfirmScanDraftOutcome.DraftRejected => Conflict(new { error = result.Error ?? "Scan draft is rejected." }),
            _ => BadRequest(new { error = result.Error ?? "Scan draft could not be confirmed." }),
        };
    }

    private IActionResult ConfirmationOk(
        ConfirmScanDraftResult result,
        AtomicCommandDisposition disposition)
    {
        var replayed = disposition is AtomicCommandDisposition.Replayed or AtomicCommandDisposition.Joined;
        var atomicDisposition = disposition.ToString();
        var entityType = Enum.TryParse<ScanConfirmationTargetKind>(
            result.TargetEntityType, ignoreCase: true, out var targetKind)
            ? targetKind.ToString()
            : result.TargetEntityType;
        var status = result.Outcome == ConfirmScanDraftOutcome.AlreadyConfirmed
            ? "alreadyConfirmed"
            : "confirmed";
        return entityType switch
        {
            "Payment" => Ok(new { receiptId = result.LedgerEntryId, entityType, entityId = result.TargetEntityId, unitId = result.UnitId, status, replayed, atomicDisposition }),
            "WorkOrder" => Ok(new { workOrderId = result.TargetEntityId, entityType, entityId = result.TargetEntityId, unitId = result.UnitId, status, replayed, atomicDisposition }),
            "Application" => Ok(new { applicationId = result.TargetEntityId, entityType, entityId = result.TargetEntityId, unitId = result.UnitId, status, replayed, atomicDisposition }),
            "Loan" => Ok(new { loanId = result.TargetEntityId, entityType, entityId = result.TargetEntityId, unitId = result.UnitId, status, replayed, atomicDisposition }),
            nameof(LeaseAgreement) => Ok(new { leaseManagementId = result.LeaseManagementId, agreementId = result.TargetEntityId, entityType = nameof(LeaseAgreement), entityId = result.TargetEntityId, unitId = result.UnitId, status, replayed, atomicDisposition }),
            _ => Ok(new { expenseId = result.TargetEntityId, entityType, entityId = result.TargetEntityId, unitId = result.UnitId, status, replayed, atomicDisposition }),
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
