using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.Scanning;       // ReceiptExtractionSchema
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Engine.Workers;

public sealed record ExtractionSchema(
    string Instructions,
    IReadOnlyList<ExtractionFieldSpec> Fields);

/// <summary>
/// Polls Pending <see cref="Core.Entities.ScanDraft"/> rows, runs LLM extraction off the request
/// path, writes the per-field {value,confidence} JSON + provenance back to the draft, flips Status
/// to "Reviewing", and notifies the web via IDataUpdateService so the review page refreshes.
/// On failure the draft is marked "Failed" so the UI can offer manual entry.
/// </summary>
public class ScanProcessingWorker : EngineWorkerBase
{
    private const int BatchSize = 10;

    protected override string WorkerName => "ScanProcessingWorker";
    // Poll quickly: the user is actively waiting on extraction, so pick up a
    // freshly-uploaded scan within ~2s instead of up to 5s. The OpenAI call is
    // the only unavoidable latency after pickup.
    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(2);
    protected override TimeSpan StepTimeout => TimeSpan.FromSeconds(90);

    public ScanProcessingWorker(IServiceProvider serviceProvider, ILogger<ScanProcessingWorker> logger)
        : base(serviceProvider, logger) { }

    public static ExtractionSchema ChooseExtractionSchema(string? targetEntityType)
        => string.Equals(targetEntityType, "WorkOrder", StringComparison.OrdinalIgnoreCase)
            ? new ExtractionSchema(WorkOrderExtractionSchema.Instructions, WorkOrderExtractionSchema.Fields)
            : IsLeaseTarget(targetEntityType)
                ? new ExtractionSchema(LeaseExtractionSchema.Instructions, LeaseExtractionSchema.Fields)
                : new ExtractionSchema(ReceiptExtractionSchema.Instructions, ReceiptExtractionSchema.Fields);

    /// <summary>
    /// A lease import is requested either by the explicit upload target "Lease", or by a document the
    /// model classified as a lease ("Lease"/"LeaseAgreement"). Both pick the lease extraction schema and
    /// confirm as a Lease.
    /// </summary>
    private static bool IsLeaseTarget(string? targetEntityType) =>
        string.Equals(targetEntityType, "Lease", StringComparison.OrdinalIgnoreCase)
        || string.Equals(targetEntityType, "LeaseAgreement", StringComparison.OrdinalIgnoreCase);

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var db = scoped.GetRequiredService<RentalCommandDbContext>();
        var llm = scoped.GetRequiredService<ILlmProvider>();
        var storage = scoped.GetRequiredService<IFileStorage>();
        var dataUpdate = scoped.GetRequiredService<IDataUpdateService>();
        var logger = scoped.GetRequiredService<ILogger<ScanProcessingWorker>>();

        var pending = await db.ScanDrafts
            .Where(d => d.Status == "Pending")
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var processed = 0;
        foreach (var draft in pending)
        {
            ct.ThrowIfCancellationRequested();

            // Tracks whether THIS draft is currently claimed in 'Processing'. If a
            // cancellation (per-cycle StepTimeout or host shutdown) interrupts the LLM
            // call below, the cancellation handler uses this to drive the claimed draft to
            // a visible terminal 'Failed' state instead of leaving it stuck in 'Processing'.
            var claimedThisDraft = false;
            try
            {
                // Atomically claim this draft: flip Pending → Processing only if it is still
                // Pending. If another Engine restart already picked it up (or another instance
                // raced us), zero rows are updated and we skip to avoid double-processing the
                // paid LLM call and double-incrementing TokensUsed/CostUsd.
                var claimed = await db.ScanDrafts
                    .Where(d => d.Id == draft.Id && d.Status == "Pending")
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "Processing"), ct);
                if (claimed == 0)
                    continue;
                claimedThisDraft = true;

                // Read the stored bytes back from the blob store.
                await using var stream = await storage.DownloadAsync(draft.FilePath, ct);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();

                // The stored key is extensionless, so prefer the real content type captured on the
                // StoredFile row at upload (PNG/HEIC would otherwise be mislabeled image/jpeg);
                // fall back to the path-based guess only if the row/type is missing.
                var storedFile = await db.StoredFiles
                    .FirstOrDefaultAsync(f => f.FilePath == draft.FilePath, ct);
                var contentType = !string.IsNullOrWhiteSpace(storedFile?.ContentType)
                    ? storedFile!.ContentType
                    : GuessContentType(draft.FilePath);

                // Build grounding context (the landlord's known vendors/properties/units/tenants)
                // so the LLM can normalise extracted names to the actual records on file
                // (e.g. match "Apex Plumbing" to the vendor row). Bounded per list to keep the
                // prompt small/cheap on large portfolios.
                var groundingContext = await BuildGroundingContextAsync(db, draft.PortfolioId, ct);

                var schema = ChooseExtractionSchema(draft.TargetEntityType);

                var extracted = await ExtractWithRetryAsync(
                    llm, bytes, contentType, schema, groundingContext, draft.Id, logger, ct);

                // Guard against the silent-empty-draft bug: a failed/empty/truncated/unparseable
                // extraction must surface as a terminal "Failed" (with a reason) so the reviewer
                // knows to re-scan or enter manually — it must NEVER be stored as a "Reviewing"
                // draft whose every field is blank with 0 confidence.
                var failureReason = GetExtractionFailureReason(extracted, schema.Fields);
                if (failureReason is not null)
                {
                    logger.LogWarning(
                        "Scan extraction produced no usable result for draft {DraftId} " +
                        "(model {ModelId}): {Reason}; marking Failed",
                        draft.Id, extracted.ModelId, failureReason);
                    await MarkFailedAsync(scoped, dataUpdate, draft.PortfolioId, draft.Id, logger, failureReason);
                    continue;
                }

                // Persist {name:{value,confidence}} JSON + provenance.
                var fieldJson = JsonSerializer.Serialize(extracted.Fields.ToDictionary(
                    kv => kv.Key,
                    kv => new { value = kv.Value.Value, confidence = kv.Value.Confidence }));
                draft.ExtractedFields = fieldJson;
                draft.FailureReason = null;
                draft.ModelId = extracted.ModelId;
                draft.TokensUsed = extracted.TokensUsed;
                draft.CostUsd = EstimateCost(extracted.ModelId, extracted.InputTokens, extracted.OutputTokens);

                if (string.Equals(draft.TargetEntityType, "WorkOrder", StringComparison.OrdinalIgnoreCase))
                {
                    draft.TargetEntityType = "WorkOrder";
                }
                else if (IsLeaseTarget(draft.TargetEntityType))
                {
                    // A lease PDF was uploaded with the Lease target (the "import your PDF leases" path):
                    // it was extracted with the lease schema, so confirm it as a Lease.
                    draft.TargetEntityType = "Lease";
                }
                else
                {
                    // Route by classified document kind: a lease agreement the model recognised becomes a
                    // Lease, rent checks become Payments, everything else Expenses. (A document classified
                    // as a lease here was extracted with the receipt schema, so the confirm step still asks
                    // the reviewer to fill the lease terms — but it lands on the correct review branch.)
                    var classifiedKind = extracted.Fields.TryGetValue("document_kind", out var kindField)
                        ? kindField.Value ?? string.Empty
                        : string.Empty;
                    draft.TargetEntityType = classifiedKind switch
                    {
                        "Lease" or "LeaseAgreement" => "Lease",
                        "RentCheck" => "Payment",
                        _ => "Expense",
                    };
                }

                draft.Status = "Reviewing";
                draft.ReviewedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Scan extraction succeeded for draft {DraftId} (model {ModelId}): " +
                    "{FieldCount} field(s) with a value → {Target}, Reviewing",
                    draft.Id, extracted.ModelId,
                    CountNonEmptyDataFields(extracted, schema.Fields), draft.TargetEntityType);

                await dataUpdate.BroadcastEntityUpdateAsync(
                    draft.PortfolioId, "ScanDraft", draft.Id,
                    new { draft.Id, draft.Status, draft.TargetEntityType }, ct);
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The cycle was cancelled mid-flight — either the per-cycle StepTimeout fired
                // (a slow/poison document) or the host is shutting down. Both cancel the same
                // token, and either way a draft we already flipped to 'Processing' would be
                // stranded there forever (the worker only ever polls 'Pending'). Drive it to a
                // visible terminal 'Failed' state so the user can retry or reject. The write must
                // NOT use the already-cancelled token, or the status update would never persist.
                if (claimedThisDraft)
                {
                    logger.LogWarning(
                        "Scan extraction interrupted (cancellation) for draft {DraftId}; marking Failed", draft.Id);
                    await MarkFailedAsync(scoped, dataUpdate, draft.PortfolioId, draft.Id, logger,
                        "extraction interrupted (timeout or shutdown)");
                }
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan extraction failed for draft {DraftId}", draft.Id);
                // Use a fresh, non-cancelled save: if the failure rode in on an already-cancelled
                // token (e.g. a timeout surfaced as a DB/HTTP cancellation), reusing it here would
                // throw again and leave the draft stuck in 'Processing'.
                await MarkFailedAsync(scoped, dataUpdate, draft.PortfolioId, draft.Id, logger,
                    "extraction failed (provider or processing error)");
            }
        }
        return processed;
    }

    // Per-list cap on grounding records so the prompt stays small/cheap even on large
    // portfolios (a few hundred records ≈ a few KB of JSON). Most extractions only need
    // a handful of candidate names to disambiguate; we order by most-recently-touched so
    // the records a landlord actually transacts with are the ones included.
    private const int GroundingCap = 150;

    /// <summary>
    /// Builds a compact JSON grounding object — { vendors, properties, units, tenants } — of the
    /// portfolio's known records so the LLM can normalise extracted names to the exact records on
    /// file AND return their primary-key ids for auto-fill. Each entry is an {id, name} pair (units
    /// also carry {unitNumber, propertyId}) so the model can hand back a real id that downstream
    /// code validates against this same set before trusting it. Active (non-soft-deleted) rows only,
    /// capped per list, read no-tracking. Returns null when the portfolio has no records to ground
    /// against (keeps the prompt unchanged in that case).
    /// </summary>
    private static async Task<string?> BuildGroundingContextAsync(
        RentalCommandDbContext db, int portfolioId, CancellationToken ct)
    {
        var vendors = await db.Vendors.AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId && v.DeletedAt == null)
            .OrderByDescending(v => v.UpdatedAt)
            .Select(v => new { id = v.Id, name = v.Name })
            .Take(GroundingCap)
            .ToListAsync(ct);

        var properties = await db.Properties.AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.DeletedAt == null)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new { id = p.Id, name = p.Name })
            .Take(GroundingCap)
            .ToListAsync(ct);

        // Units are scoped through their Property (Unit has no PortfolioId of its own).
        var units = await db.Units.AsNoTracking()
            .Where(u => u.DeletedAt == null
                        && db.Properties.Any(p => p.Id == u.PropertyId
                                                  && p.PortfolioId == portfolioId
                                                  && p.DeletedAt == null))
            .OrderByDescending(u => u.UpdatedAt)
            .Select(u => new { id = u.Id, unitNumber = u.UnitNumber, propertyId = u.PropertyId })
            .Take(GroundingCap)
            .ToListAsync(ct);

        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.DeletedAt == null)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new { id = t.Id, name = (t.FirstName + " " + t.LastName).Trim() })
            .Take(GroundingCap)
            .ToListAsync(ct);

        if (vendors.Count == 0 && properties.Count == 0 && units.Count == 0 && tenants.Count == 0)
            return null;

        return JsonSerializer.Serialize(new { vendors, properties, units, tenants });
    }

    /// <summary>
    /// Drives a claimed ('Processing') draft to the terminal 'Failed' state on a fresh DbContext
    /// scope with a non-cancellable token, so the write persists even when the cycle's token is
    /// already cancelled (StepTimeout / shutdown) or its DbContext is in a faulted state. Only
    /// flips rows still in 'Processing' so it never clobbers a state the API or a later cycle set.
    /// </summary>
    private static async Task MarkFailedAsync(
        IServiceProvider scoped,
        IDataUpdateService dataUpdate,
        int portfolioId,
        int draftId,
        ILogger logger,
        string? failureReason = null)
    {
        // Keep the stored reason within the column's bound (no silent truncation surprises).
        var reason = failureReason is { Length: > 0 }
            ? (failureReason.Length > 500 ? failureReason[..500] : failureReason)
            : null;
        try
        {
            using var failScope = scoped.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var failDb = failScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            await failDb.ScanDrafts
                .Where(d => d.Id == draftId && d.Status == "Processing")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, "Failed")
                    .SetProperty(d => d.FailureReason, reason)
                    .SetProperty(d => d.ReviewedAt, DateTime.UtcNow), CancellationToken.None);

            await dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId, "ScanDraft", draftId,
                new { Id = draftId, Status = "Failed", FailureReason = reason }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Last-resort: never let the failure-handling itself throw out of the cycle. The
            // startup crash-recovery (Processing → Pending) is the backstop if this never lands.
            logger.LogError(ex, "Failed to mark draft {DraftId} as Failed", draftId);
        }
    }

    // Field names that classify the document but carry no extracted *data*: a model that read
    // nothing still picks a document_kind, so an extraction whose only non-empty field is the
    // classifier is effectively empty and must not be stored as a ready-to-review draft.
    private static readonly HashSet<string> ClassifierOnlyFields =
        new(StringComparer.OrdinalIgnoreCase) { "document_kind" };

    /// <summary>
    /// Returns a concise reason this extraction must be treated as a failure, or <c>null</c> when it
    /// carries at least one real extracted value and should proceed to "Reviewing". Surfaces, in
    /// order: an explicit provider failure (truncation / unparseable / no model), then the
    /// "empty skeleton" case (every data field blank/whitespace — the silent-empty-draft bug).
    /// Pure and deterministic so it is unit-testable without the database or a live provider.
    /// </summary>
    public static string? GetExtractionFailureReason(
        ExtractedFields extracted, IReadOnlyList<ExtractionFieldSpec> fields)
    {
        if (extracted is null)
            return "extraction returned no result";

        // A provider that flagged a hard failure (truncated, unparseable, unavailable) wins even if
        // a stray field slipped through, because the result can't be trusted as complete.
        if (!string.IsNullOrWhiteSpace(extracted.FailureReason))
            return extracted.FailureReason;

        if (CountNonEmptyDataFields(extracted, fields) == 0)
            return "no fields could be extracted from the document";

        return null;
    }

    /// <summary>
    /// Counts how many real <em>data</em> fields came back with a non-whitespace value, ignoring
    /// the classifier-only fields (e.g. document_kind) that a model fills even when it read nothing.
    /// </summary>
    private static int CountNonEmptyDataFields(
        ExtractedFields extracted, IReadOnlyList<ExtractionFieldSpec> fields)
    {
        if (extracted?.Fields is not { Count: > 0 }) return 0;

        // Restrict to the schema we asked for so an unexpected extra key can't mask an empty result.
        var dataFieldNames = fields
            .Select(f => f.Name)
            .Where(n => !ClassifierOnlyFields.Contains(n));

        var count = 0;
        foreach (var name in dataFieldNames)
        {
            if (extracted.Fields.TryGetValue(name, out var fe)
                && !string.IsNullOrWhiteSpace(fe?.Value))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Runs the extraction with a single bounded retry on a <em>transient</em> provider error
    /// (network blip / HTTP 429 / 5xx surfaced as an <see cref="HttpRequestException"/>). A genuine
    /// empty/unparseable result is NOT retried here — it returns and the caller fails the draft with
    /// a reason. Honours the cycle token (a real cancellation/timeout propagates as before).
    /// </summary>
    private static async Task<ExtractedFields> ExtractWithRetryAsync(
        ILlmProvider llm,
        byte[] bytes,
        string contentType,
        ExtractionSchema schema,
        string? groundingContext,
        int draftId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            return await llm.ExtractAsync(
                bytes, contentType, schema.Instructions, schema.Fields, groundingContext, ct);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            // One short backoff then a single retry; a second failure propagates to the catch in
            // ExecuteCycleAsync, which marks the draft Failed (never silently Reviewing).
            logger.LogWarning(ex,
                "Transient extraction error for draft {DraftId}; retrying once", draftId);
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
            return await llm.ExtractAsync(
                bytes, contentType, schema.Instructions, schema.Fields, groundingContext, ct);
        }
    }

    private static string GuessContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".heic" => "image/heic",
            _ => "image/jpeg"
        };

    // Approximate list-price estimates (USD per 1M tokens) for display only — not billing-accurate.
    // Rates are for common hosted models; unknown models use a conservative 1.00/3.00 default.
    private static decimal EstimateCost(string modelId, int inputTokens, int outputTokens)
    {
        if (inputTokens == 0 && outputTokens == 0) return 0m;
        var id = modelId ?? string.Empty;
        decimal inRate, outRate;
        if (id.Contains("gpt-4o-mini", StringComparison.OrdinalIgnoreCase))
            { inRate = 0.15m; outRate = 0.60m; }
        else if (id.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase))
            { inRate = 2.50m; outRate = 10.00m; }
        else if (id.Contains("claude", StringComparison.OrdinalIgnoreCase))
            { inRate = 3.00m; outRate = 15.00m; }
        else if (id.Equals("noop", StringComparison.OrdinalIgnoreCase))
            return 0m;
        else
            { inRate = 1.00m; outRate = 3.00m; }
        return Math.Round(inputTokens / 1_000_000m * inRate + outputTokens / 1_000_000m * outRate, 6);
    }
}
