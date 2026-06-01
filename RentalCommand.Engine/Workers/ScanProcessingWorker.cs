using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.Scanning;       // ReceiptExtractionSchema
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Polls Pending <see cref="Core.Entities.ScanDraft"/> rows, runs LLM extraction off the request
/// path, writes the per-field {value,confidence} JSON + provenance back to the draft, flips Status
/// to "Reviewing", and notifies the web via IDataUpdateService so the review page refreshes.
/// On failure the draft is marked "Failed" so the UI can offer manual entry.
/// </summary>
public sealed class ScanProcessingWorker : EngineWorkerBase
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

                // Receipt→Expense only this phase.
                var extracted = await llm.ExtractAsync(
                    bytes, contentType,
                    ReceiptExtractionSchema.Instructions,
                    ReceiptExtractionSchema.Fields,
                    groundingContext, ct);

                // Persist {name:{value,confidence}} JSON + provenance.
                var fieldJson = JsonSerializer.Serialize(extracted.Fields.ToDictionary(
                    kv => kv.Key,
                    kv => new { value = kv.Value.Value, confidence = kv.Value.Confidence }));
                draft.ExtractedFields = fieldJson;
                draft.ModelId = extracted.ModelId;
                draft.TokensUsed = extracted.TokensUsed;
                draft.CostUsd = EstimateCost(extracted.ModelId, extracted.InputTokens, extracted.OutputTokens);

                // Route by classified document kind: rent checks become Payments, everything else Expenses.
                var classifiedKind = extracted.Fields.TryGetValue("document_kind", out var kindField)
                    ? kindField.Value ?? string.Empty
                    : string.Empty;
                draft.TargetEntityType = classifiedKind == "RentCheck" ? "Payment" : "Expense";

                draft.Status = "Reviewing";
                draft.ReviewedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

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
                    await MarkFailedAsync(scoped, dataUpdate, draft.PortfolioId, draft.Id, logger);
                }
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan extraction failed for draft {DraftId}", draft.Id);
                // Use a fresh, non-cancelled save: if the failure rode in on an already-cancelled
                // token (e.g. a timeout surfaced as a DB/HTTP cancellation), reusing it here would
                // throw again and leave the draft stuck in 'Processing'.
                await MarkFailedAsync(scoped, dataUpdate, draft.PortfolioId, draft.Id, logger);
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
    /// portfolio's known record names so the LLM can normalise extracted names to the exact
    /// spellings on file. Active (non-soft-deleted) rows only, capped per list, read no-tracking.
    /// Returns null when the portfolio has no records to ground against (keeps the prompt
    /// unchanged in that case).
    /// </summary>
    private static async Task<string?> BuildGroundingContextAsync(
        RentalCommandDbContext db, int portfolioId, CancellationToken ct)
    {
        var vendors = await db.Vendors.AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId && v.DeletedAt == null)
            .OrderByDescending(v => v.UpdatedAt)
            .Select(v => v.Name)
            .Take(GroundingCap)
            .ToListAsync(ct);

        var properties = await db.Properties.AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.DeletedAt == null)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => p.Name)
            .Take(GroundingCap)
            .ToListAsync(ct);

        // Units are scoped through their Property (Unit has no PortfolioId of its own).
        var units = await db.Units.AsNoTracking()
            .Where(u => u.DeletedAt == null
                        && db.Properties.Any(p => p.Id == u.PropertyId
                                                  && p.PortfolioId == portfolioId
                                                  && p.DeletedAt == null))
            .OrderByDescending(u => u.UpdatedAt)
            .Select(u => u.UnitNumber)
            .Take(GroundingCap)
            .ToListAsync(ct);

        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.DeletedAt == null)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => (t.FirstName + " " + t.LastName).Trim())
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
        ILogger logger)
    {
        try
        {
            using var failScope = scoped.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var failDb = failScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            await failDb.ScanDrafts
                .Where(d => d.Id == draftId && d.Status == "Processing")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, "Failed")
                    .SetProperty(d => d.ReviewedAt, DateTime.UtcNow), CancellationToken.None);

            await dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId, "ScanDraft", draftId,
                new { Id = draftId, Status = "Failed" }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Last-resort: never let the failure-handling itself throw out of the cycle. The
            // startup crash-recovery (Processing → Pending) is the backstop if this never lands.
            logger.LogError(ex, "Failed to mark draft {DraftId} as Failed", draftId);
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
