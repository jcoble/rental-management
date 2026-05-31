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
            try
            {
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

                // Receipt→Expense only this phase.
                var extracted = await llm.ExtractAsync(
                    bytes, contentType,
                    ReceiptExtractionSchema.Instructions,
                    ReceiptExtractionSchema.Fields,
                    groundingContext: null, ct);

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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan extraction failed for draft {DraftId}", draft.Id);
                draft.Status = "Failed";
                draft.ReviewedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await dataUpdate.BroadcastEntityUpdateAsync(
                    draft.PortfolioId, "ScanDraft", draft.Id,
                    new { draft.Id, draft.Status }, CancellationToken.None);
            }
        }
        return processed;
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
