using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Scanning;       // ReceiptExtractionSchema
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Scanning;

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
public class ScanProcessingWorker : EngineWorkerBase, IScanProcessingCycleService
{
    // Extraction is remote and sequential. Claim one row so a cycle timeout cannot strand later
    // rows from a pre-claimed batch; the next cycle claims the next oldest row.
    private const int BatchSize = 1;
    private readonly string _claimOwner = $"{Environment.MachineName}:{Environment.ProcessId}:scan:{Guid.NewGuid():N}";
    private readonly TimeSpan _claimLease;
    private readonly TimeSpan _stepTimeout;

    protected override string WorkerName => "ScanProcessingWorker";
    // Poll quickly: the user is actively waiting on extraction, so pick up a
    // freshly-uploaded scan within ~2s instead of up to 5s. Provider extraction is
    // the only unavoidable latency after pickup.
    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(2);
    protected override TimeSpan StepTimeout => _stepTimeout;

    public ScanProcessingWorker(IServiceProvider serviceProvider, ILogger<ScanProcessingWorker> logger)
        : base(serviceProvider, logger)
    {
        var environment = serviceProvider.GetRequiredService<IHostEnvironment>();
        var assistant = serviceProvider.GetRequiredService<IOptions<AssistantConfig>>().Value;
        _claimLease = ResolveClaimLease(environment.EnvironmentName, assistant.Provider);
        _stepTimeout = ResolveStepTimeout(environment.EnvironmentName, assistant.Provider);
    }

    internal static bool ShouldUseDevelopmentSubscriptionProvider(
        string environmentName,
        string? provider) =>
        string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
        && string.Equals(provider, "claude-cli", StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan ResolveStepTimeout(string environmentName, string? provider) =>
        ShouldUseDevelopmentSubscriptionProvider(environmentName, provider)
            ? TimeSpan.FromSeconds(210)
            : TimeSpan.FromSeconds(90);

    internal static TimeSpan ResolveClaimLease(string environmentName, string? provider) =>
        ShouldUseDevelopmentSubscriptionProvider(environmentName, provider)
            ? TimeSpan.FromMinutes(4)
            : TimeSpan.FromMinutes(2);

    public static ExtractionSchema ChooseExtractionSchema(string? targetEntityType)
        => string.Equals(targetEntityType, "WorkOrder", StringComparison.OrdinalIgnoreCase)
            ? new ExtractionSchema(WorkOrderExtractionSchema.Instructions, WorkOrderExtractionSchema.Fields)
            : IsLeaseTarget(targetEntityType)
                ? new ExtractionSchema(LeaseExtractionSchema.Instructions, LeaseExtractionSchema.Fields)
                : IsApplicationTarget(targetEntityType)
                    ? new ExtractionSchema(ApplicationExtractionSchema.Instructions, ApplicationExtractionSchema.Fields)
                    : IsLoanTarget(targetEntityType)
                        ? new ExtractionSchema(LoanExtractionSchema.Instructions, LoanExtractionSchema.Fields)
                        : IsLeaseEndingNoticeTarget(targetEntityType)
                            ? new ExtractionSchema(LeaseEndingNoticeExtractionSchema.Instructions, LeaseEndingNoticeExtractionSchema.Fields)
                            : new ExtractionSchema(ReceiptExtractionSchema.Instructions, ReceiptExtractionSchema.Fields);

    private static readonly HashSet<string> SupportedTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Expense", "Payment", "WorkOrder", nameof(LeaseAgreement), "Application", "Loan", "LeaseEndingNotice",
    };

    /// <summary>
    /// A lease import uses the explicit canonical upload target "LeaseAgreement".
    /// </summary>
    private static bool IsLeaseTarget(string? targetEntityType) =>
        string.Equals(targetEntityType, "LeaseAgreement", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An application import is requested by the explicit upload target "Application" (a landlord scanning
    /// a completed paper rental application). It picks the application extraction schema and confirms as an
    /// Application — the scan-IN counterpart of the public apply form.
    /// </summary>
    private static bool IsApplicationTarget(string? targetEntityType) =>
        string.Equals(targetEntityType, "Application", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A loan import is requested by the explicit upload target "Loan" (a landlord scanning a mortgage
    /// statement or closing disclosure). It picks the loan extraction schema and confirms as a Loan on
    /// the property — the scan-IN counterpart of the manual loan form.
    /// </summary>
    private static bool IsLoanTarget(string? targetEntityType) =>
        string.Equals(targetEntityType, "Loan", StringComparison.OrdinalIgnoreCase);

    private static bool IsLeaseEndingNoticeTarget(string? targetEntityType) =>
        string.Equals(targetEntityType, "LeaseEndingNotice", StringComparison.OrdinalIgnoreCase);

    protected override Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
        => RunOneCycleAsync(scoped, ct);

    public async Task<int> RunOneCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var db = scoped.GetRequiredService<RentalCommandDbContext>();
        var credentialResolver = scoped.GetRequiredService<IWorkspaceLlmCredentialResolver>();
        var usageRecorder = scoped.GetRequiredService<ILlmUsageEvidenceRecorder>();
        var workspaceProviders = scoped.GetServices<IWorkspaceLlmExtractionProvider>()
            .ToDictionary(provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);
        var storage = scoped.GetRequiredService<IFileStorage>();
        var writes = scoped.GetRequiredService<IWriteExecutor>();
        var timeProvider = scoped.GetRequiredService<TimeProvider>();
        var logger = scoped.GetRequiredService<ILogger<ScanProcessingWorker>>();
        var claimStore = scoped.GetRequiredService<IScanProcessingClaimStore>();
        var assistant = scoped.GetRequiredService<IOptions<AssistantConfig>>().Value;
        var environment = scoped.GetRequiredService<IHostEnvironment>();
        var useDevelopmentSubscriptionProvider = ShouldUseDevelopmentSubscriptionProvider(
            environment.EnvironmentName,
            assistant.Provider);
        var developmentSubscriptionProvider = useDevelopmentSubscriptionProvider
            ? scoped.GetRequiredService<ILlmProvider>()
            : null;

        var pending = await claimStore.ClaimAsync(
            _claimOwner, _claimLease, BatchSize, ct);
        if (pending.Count == 0) return 0;

        var processed = 0;
        foreach (var draft in pending)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                WorkspaceLlmRuntimeCredential? credential = null;
                IWorkspaceLlmExtractionProvider? workspaceProvider = null;
                if (!useDevelopmentSubscriptionProvider)
                {
                    credential = await credentialResolver.ResolveActiveAsync(
                        draft.PortfolioId, ct);
                    if (credential is null)
                    {
                        await MarkFailedAsync(
                            scoped, draft.PortfolioId, draft.Id,
                            draft.ClaimOwner, draft.ClaimToken, logger,
                            "AI extraction unavailable: configure a workspace OpenAI or Anthropic credential in Settings");
                        continue;
                    }

                    if (!workspaceProviders.TryGetValue(
                            credential.Provider,
                            out workspaceProvider))
                    {
                        await MarkFailedAsync(
                            scoped, draft.PortfolioId, draft.Id,
                            draft.ClaimOwner, draft.ClaimToken, logger,
                            "AI extraction unavailable: the configured workspace provider is not supported");
                        continue;
                    }
                }

                // Read the stored bytes back from the blob store.
                await using var stream = await storage.DownloadAsync(draft.FilePath, ct);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();

                // The stored key is extensionless, so prefer the real content type captured on the
                // StoredFile row at upload (PNG/HEIC would otherwise be mislabeled image/jpeg);
                // fall back to the path-based guess only if the row/type is missing.
                var storedFile = draft.SourceStoredFileId is int sourceStoredFileId
                    ? await db.StoredFiles.AsNoTracking()
                        .FirstOrDefaultAsync(f =>
                            f.Id == sourceStoredFileId && f.PortfolioId == draft.PortfolioId, ct)
                    : null;
                var contentType = !string.IsNullOrWhiteSpace(storedFile?.ContentType)
                    ? storedFile!.ContentType
                    : GuessContentType(draft.FilePath);

                // Build grounding context (the landlord's known vendors/properties/units/tenants)
                // so the LLM can normalise extracted names to the actual records on file
                // (e.g. match "Apex Plumbing" to the vendor row). Bounded per list to keep the
                // prompt small/cheap on large portfolios.
                var groundingContext = await BuildGroundingContextAsync(db, draft.PortfolioId, ct);
                var usageInvocationCounts = new Dictionary<string, int>(StringComparer.Ordinal);

                Task<ExtractedFields> InvokeExtractionAsync(
                    ExtractionSchema extractionSchema,
                    string feature,
                    CancellationToken token)
                {
                    var invocation = usageInvocationCounts.TryGetValue(feature, out var prior)
                        ? prior + 1
                        : 1;
                    usageInvocationCounts[feature] = invocation;
                    var usageEventIdentity =
                        $"scan:{draft.Id}:claim:{draft.ClaimToken:N}:{feature}:{invocation}";
                    return useDevelopmentSubscriptionProvider
                        ? InvokeDevelopmentSubscriptionExtractionAsync(
                            developmentSubscriptionProvider!,
                            usageRecorder,
                            draft.PortfolioId,
                            feature,
                            usageEventIdentity,
                            bytes,
                            contentType,
                            extractionSchema,
                            groundingContext,
                            token)
                        : InvokeWorkspaceExtractionAsync(
                            workspaceProvider!,
                            credential!,
                            usageRecorder,
                            draft.PortfolioId,
                            feature,
                            usageEventIdentity,
                            bytes,
                            contentType,
                            extractionSchema,
                            groundingContext,
                            token);
                }

                var resolvedTargetEntityType = draft.TargetEntityType;
                ExtractedFields? classification = null;
                if (string.IsNullOrWhiteSpace(resolvedTargetEntityType))
                {
                    var classificationSchema = new ExtractionSchema(
                        DocumentClassificationExtractionSchema.Instructions,
                        DocumentClassificationExtractionSchema.Fields);
                    classification = await ExtractWithRetryAsync(
                        token => InvokeExtractionAsync(
                            classificationSchema,
                            "scan.classification",
                            token),
                        draft.Id,
                        logger,
                        ct);
                    var classificationFailure = GetExtractionFailureReason(
                        classification, classificationSchema.Fields);
                    if (classificationFailure is not null
                        || !classification.Fields.TryGetValue("target_entity_type", out var targetField)
                        || !SupportedTargets.Contains(targetField.Value))
                    {
                        await MarkFailedAsync(
                            scoped, draft.PortfolioId, draft.Id,
                            draft.ClaimOwner, draft.ClaimToken, logger,
                            classificationFailure ?? "document destination could not be classified");
                        continue;
                    }

                    resolvedTargetEntityType = SupportedTargets.First(target =>
                        string.Equals(target, targetField.Value, StringComparison.OrdinalIgnoreCase));
                    logger.LogInformation(
                        "Scan draft {DraftId} classified as {Target}; starting typed extraction",
                        draft.Id, resolvedTargetEntityType);
                }

                var schema = ChooseExtractionSchema(resolvedTargetEntityType);
                var extracted = await ExtractWithRetryAsync(
                    token => InvokeExtractionAsync(
                        schema,
                        "scan.extraction",
                        token),
                    draft.Id,
                    logger,
                    ct);

                // Preserve classification evidence in the review proposal, but never treat it as
                // authority. Confirmation still validates current session, capability, and scope.
                if (classification is not null)
                {
                    CopyClassificationField(classification, extracted, "document_kind");
                    CopyClassificationField(classification, extracted, "classification_reason");
                    extracted.InputTokens += classification.InputTokens;
                    extracted.OutputTokens += classification.OutputTokens;
                    extracted.TokensUsed += classification.TokensUsed;
                    extracted.ModelId = string.IsNullOrWhiteSpace(extracted.ModelId)
                        ? classification.ModelId
                        : extracted.ModelId;
                }

                if (string.IsNullOrWhiteSpace(extracted.FailureReason))
                {
                    var repairInstruction = GetExtractionQualityRepairInstruction(resolvedTargetEntityType, extracted);
                    if (repairInstruction is not null)
                    {
                        logger.LogInformation(
                            "Scan extraction for draft {DraftId} needs quality repair: {RepairInstruction}",
                            draft.Id, repairInstruction);
                        try
                        {
                            var repairSchema = new ExtractionSchema(
                                schema.Instructions +
                                "\n\nQUALITY REPAIR PASS: " + repairInstruction +
                                " Return the full extraction schema again. Keep previously correct fields unchanged; " +
                                "only improve fields you can read from the document.",
                                schema.Fields);
                            var repaired = await InvokeExtractionAsync(
                                repairSchema,
                                "scan.quality-repair",
                                ct);
                            ApplyQualityRepair(resolvedTargetEntityType, extracted, repaired);
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            logger.LogWarning(ex,
                                "Scan extraction quality repair failed for draft {DraftId}; keeping initial extraction",
                                draft.Id);
                        }
                    }

                    NormalizeExtractionQuality(resolvedTargetEntityType, extracted);
                }

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
                    await MarkFailedAsync(
                        scoped, draft.PortfolioId, draft.Id,
                        draft.ClaimOwner, draft.ClaimToken, logger, failureReason);
                    continue;
                }

                // Persist {name:{value,confidence}} JSON + provenance.
                var fieldJson = JsonSerializer.Serialize(extracted.Fields.ToDictionary(
                    kv => kv.Key,
                    kv => new { value = kv.Value.Value, confidence = kv.Value.Confidence }));
                var targetEntityType = resolvedTargetEntityType;

                var reviewedAt = timeProvider.UtcNow();
                var command = new ScanProcessingTerminalCommand(
                    draft.Id,
                    draft.PortfolioId,
                    draft.ClaimOwner,
                    draft.ClaimToken,
                    "Reviewing",
                    reviewedAt,
                    new ScanProcessingResult(
                        fieldJson,
                        extracted.ModelId,
                        extracted.TokensUsed,
                        EstimateCost(extracted.ModelId, extracted.InputTokens, extracted.OutputTokens),
                        targetEntityType,
                        reviewedAt));
                var completed = await writes.ExecuteAsync(
                    ScanProcessingTerminalWrite.StepKey(command),
                    ScanProcessingTerminalWrite.Write(db, command),
                    ct);
                if (!completed.Value.Applied)
                {
                    logger.LogWarning(
                        "Discarded stale scan extraction completion for draft {DraftId}; its claim lease was lost",
                        draft.Id);
                    continue;
                }

                logger.LogInformation(
                    "Scan extraction succeeded for draft {DraftId} (model {ModelId}): " +
                    "{FieldCount} field(s) with a value → {Target}, Reviewing",
                    draft.Id, extracted.ModelId,
                    CountNonEmptyDataFields(extracted, schema.Fields), targetEntityType);

                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The cycle was cancelled mid-flight — either the per-cycle StepTimeout fired
                // (a slow/poison document) or the host is shutting down. Both cancel the same
                // token, and either way a draft we already flipped to 'Processing' would be
                // remain leased until expiry. Drive the currently-owned claim to a visible terminal
                // 'Failed' state so the user can retry or reject. The write must
                // NOT use the already-cancelled token, or the status update would never persist.
                logger.LogWarning(
                    "Scan extraction interrupted (cancellation) for draft {DraftId}; marking Failed", draft.Id);
                await MarkFailedAsync(
                    scoped, draft.PortfolioId, draft.Id,
                    draft.ClaimOwner, draft.ClaimToken, logger,
                    "extraction interrupted (timeout or shutdown)");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan extraction failed for draft {DraftId}", draft.Id);
                // Use a fresh, non-cancelled save: if the failure rode in on an already-cancelled
                // token (e.g. a timeout surfaced as a DB/HTTP cancellation), reusing it here would
                // throw again and leave the draft stuck in 'Processing'.
                await MarkFailedAsync(scoped, draft.PortfolioId, draft.Id,
                    draft.ClaimOwner, draft.ClaimToken, logger,
                    "extraction failed (provider or processing error)");
            }
        }
        return processed;
    }

    private static void CopyClassificationField(
        ExtractedFields classification,
        ExtractedFields extracted,
        string fieldName)
    {
        if (!extracted.Fields.ContainsKey(fieldName)
            && classification.Fields.TryGetValue(fieldName, out var field)
            && !string.IsNullOrWhiteSpace(field.Value))
        {
            extracted.Fields[fieldName] = field;
        }
    }

    // Per-list cap on grounding records so the prompt stays small/cheap even on large
    // portfolios (a few hundred records ≈ a few KB of JSON). Most extractions only need
    // a handful of candidate names to disambiguate; we order by most-recently-touched so
    // the records a landlord actually transacts with are the ones included.
    private const int GroundingCap = 150;

    /// <summary>
    /// Builds a compact JSON grounding object — { vendors, properties, units, tenants, leaseManagements } — of the
    /// portfolio's known records so the LLM can normalise extracted names to the exact records on
    /// file AND return their primary-key ids for auto-fill. Each entry is an {id, name} pair (units
    /// also carry canonical relationship/agreement ids) so the model can hand back a real id that downstream
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

        // Carry the address (line1 + city) alongside the name so the lease importer's LLM can
        // match a scanned lease's leased-premises address to an existing property by address, not
        // just by a building name the lease may not even mention.
        var properties = await db.Properties.AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.DeletedAt == null)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new { id = p.Id, name = p.Name, addressLine1 = p.AddressLine1, city = p.City })
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

        var leaseManagements = await (
            from lifecycle in db.LeaseManagementLifecycleProjections.AsNoTracking()
            join management in db.LeaseManagements.AsNoTracking()
                on new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                equals new { management.PortfolioId, LeaseManagementId = management.Id }
            join agreement in db.LeaseAgreements.AsNoTracking()
                on new { lifecycle.PortfolioId, AgreementId = lifecycle.CurrentAgreementId }
                equals new { agreement.PortfolioId, AgreementId = (int?)agreement.Id }
            where lifecycle.PortfolioId == portfolioId
                && lifecycle.Lifecycle != "Canceled"
                && lifecycle.Lifecycle != "Closed"
            orderby management.UpdatedAtUtc descending
            select new
            {
                id = management.Id,
                leaseManagementId = management.Id,
                leaseAgreementId = agreement.Id,
                relationshipNumber = management.RelationshipNumber,
                agreementNumber = agreement.AgreementNumber,
                propertyId = management.PropertyId,
                unitId = management.UnitId,
                tenantId = lifecycle.CurrentPrimaryTenantId,
                tenantName = lifecycle.CurrentPrimaryTenantName,
            })
            .Take(GroundingCap)
            .ToListAsync(ct);

        if (vendors.Count == 0 && properties.Count == 0 && units.Count == 0 && tenants.Count == 0 && leaseManagements.Count == 0)
            return null;

        return JsonSerializer.Serialize(new { vendors, properties, units, tenants, leaseManagements });
    }

    /// <summary>
    /// Drives a claimed ('Processing') draft to the terminal 'Failed' state on a fresh DbContext
    /// scope with a non-cancellable token, so the write persists even when the cycle's token is
    /// already cancelled (StepTimeout / shutdown) or its DbContext is in a faulted state. Only
    /// flips rows still in 'Processing' so it never clobbers a state the API or a later cycle set.
    /// </summary>
    private static async Task MarkFailedAsync(
        IServiceProvider scoped,
        int portfolioId,
        int draftId,
        string claimOwner,
        Guid claimToken,
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
            var failWrites = failScope.ServiceProvider.GetRequiredService<IWriteExecutor>();
            // Hoist "now" so the command fingerprint and stored terminal timestamp are stable.
            var reviewedAt = failScope.ServiceProvider.GetRequiredService<TimeProvider>().UtcNow();
            var command = new ScanProcessingTerminalCommand(
                draftId, portfolioId, claimOwner, claimToken, "Failed", reviewedAt,
                FailureReason: reason);
            var completed = await failWrites.ExecuteAsync(
                ScanProcessingTerminalWrite.StepKey(command),
                ScanProcessingTerminalWrite.Write(failDb, command),
                CancellationToken.None);
            if (!completed.Value.Applied)
            {
                logger.LogWarning(
                    "Discarded stale scan failure completion for draft {DraftId}; its claim lease was lost",
                    draftId);
                return;
            }
        }
        catch (Exception ex)
        {
            // Last-resort: never let the failure-handling itself throw out of the cycle. The
            // Lease expiry is the backstop if this never lands.
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

    public static string? GetExtractionQualityRepairInstruction(string? targetEntityType, ExtractedFields extracted)
    {
        if (!IsExpenseLikeTarget(targetEntityType))
            return null;

        if (IsRentCheck(extracted))
            return null;

        if (NeedsReceiptLineItemRepair(extracted))
        {
            return "Re-read the receipt/invoice line-item table. For every printed row, extract description, quantity, unit_price, and amount where visible. Do not invent amounts; leave a cell blank if the image does not show it.";
        }

        return null;
    }

    public static void NormalizeExtractionQuality(string? targetEntityType, ExtractedFields extracted)
    {
        if (extracted.Fields.Count == 0 || !IsLeaseTarget(targetEntityType))
            return;

        BlankInvalidLeaseUnitCount(extracted, "unit_bedrooms", maxReasonable: 20m);
        BlankInvalidLeaseUnitCount(extracted, "unit_bathrooms", maxReasonable: 20m);
    }

    public static void ApplyQualityRepair(string? targetEntityType, ExtractedFields original, ExtractedFields repaired)
    {
        if (repaired is null || !string.IsNullOrWhiteSpace(repaired.FailureReason))
            return;

        if (IsExpenseLikeTarget(targetEntityType)
            && TryGetField(original, "line_items", out var originalLineItems)
            && TryGetField(repaired, "line_items", out var repairedLineItems)
            && ReceiptLineItemCompletenessScore(repairedLineItems.Value) > ReceiptLineItemCompletenessScore(originalLineItems.Value))
        {
            original.Fields["line_items"] = repairedLineItems;
        }

        if (IsLeaseTarget(targetEntityType))
        {
            CopyValidLeaseUnitCount(original, repaired, "unit_bedrooms", maxReasonable: 20m);
            CopyValidLeaseUnitCount(original, repaired, "unit_bathrooms", maxReasonable: 20m);
        }
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

    private static bool IsExpenseLikeTarget(string? targetEntityType) =>
        string.IsNullOrWhiteSpace(targetEntityType)
        || string.Equals(targetEntityType, "Expense", StringComparison.OrdinalIgnoreCase)
        || string.Equals(targetEntityType, "Receipt", StringComparison.OrdinalIgnoreCase)
        || string.Equals(targetEntityType, "Bill", StringComparison.OrdinalIgnoreCase)
        || string.Equals(targetEntityType, "Invoice", StringComparison.OrdinalIgnoreCase);

    private static bool IsRentCheck(ExtractedFields extracted) =>
        TryGetField(extracted, "document_kind", out var kind)
        && string.Equals(kind.Value?.Trim(), "RentCheck", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsReceiptLineItemRepair(ExtractedFields extracted)
    {
        if (!TryGetField(extracted, "line_items", out var lineItems)
            || string.IsNullOrWhiteSpace(lineItems.Value))
        {
            return false;
        }

        return ReceiptLineItemsHaveDescriptionsMissingAmounts(lineItems.Value)
            || (lineItems.Confidence is > 0m and < 0.65m
                && ReceiptLineItemCompletenessScore(lineItems.Value) > 0);
    }

    private static bool ReceiptLineItemsHaveDescriptionsMissingAmounts(string lineItemsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(lineItemsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var hasDescription = HasNonEmptyString(item, "description");
                var hasAmount = HasNumericValue(item, "amount");
                if (hasDescription && !hasAmount)
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static int ReceiptLineItemCompletenessScore(string? lineItemsJson)
    {
        if (string.IsNullOrWhiteSpace(lineItemsJson))
            return 0;

        try
        {
            using var doc = JsonDocument.Parse(lineItemsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return 0;

            var score = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (HasNonEmptyString(item, "description")) score += 2;
                if (HasNumericValue(item, "quantity")) score += 1;
                if (HasNumericValue(item, "unit_price")) score += 2;
                if (HasNumericValue(item, "amount")) score += 5;
            }
            return score;
        }
        catch
        {
            return 0;
        }
    }

    private static bool HasNonEmptyString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool HasNumericValue(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return false;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _))
            return true;

        return value.ValueKind == JsonValueKind.String
               && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Any,
                   System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static void BlankInvalidLeaseUnitCount(
        ExtractedFields extracted, string fieldName, decimal maxReasonable)
    {
        if (!TryGetField(extracted, fieldName, out var field))
            return;

        if (!decimal.TryParse(field.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value <= 0m
            || value > maxReasonable)
        {
            field.Value = string.Empty;
            field.Confidence = 0m;
        }
    }

    private static void CopyValidLeaseUnitCount(
        ExtractedFields original, ExtractedFields repaired, string fieldName, decimal maxReasonable)
    {
        if (!TryGetField(repaired, fieldName, out var repairedField))
            return;

        if (decimal.TryParse(repairedField.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value > 0m
            && value <= maxReasonable)
        {
            original.Fields[fieldName] = repairedField;
        }
    }

    private static bool TryGetField(
        ExtractedFields extracted, string fieldName, out FieldExtraction field) =>
        extracted.Fields.TryGetValue(fieldName, out field!) && field is not null;

    /// <summary>
    /// Runs the extraction with a single bounded retry on a <em>transient</em> provider error
    /// (network blip / HTTP 429 / 5xx surfaced as an <see cref="HttpRequestException"/>). A genuine
    /// empty/unparseable result is NOT retried here — it returns and the caller fails the draft with
    /// a reason. Honours the cycle token (a real cancellation/timeout propagates as before).
    /// </summary>
    private static async Task<ExtractedFields> ExtractWithRetryAsync(
        Func<CancellationToken, Task<ExtractedFields>> invoke,
        int draftId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            return await invoke(ct);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            // One short backoff then a single retry; a second failure propagates to the catch in
            // ExecuteCycleAsync, which marks the draft Failed (never silently Reviewing).
            logger.LogWarning(ex,
                "Transient extraction error for draft {DraftId}; retrying once", draftId);
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
            return await invoke(ct);
        }
    }

    private static async Task<ExtractedFields> InvokeDevelopmentSubscriptionExtractionAsync(
        ILlmProvider provider,
        ILlmUsageEvidenceRecorder usageRecorder,
        int portfolioId,
        string feature,
        string usageEventIdentity,
        byte[] bytes,
        string contentType,
        ExtractionSchema schema,
        string? groundingContext,
        CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        ExtractedFields? result = null;
        try
        {
            result = await provider.ExtractAsync(
                bytes,
                contentType,
                schema.Instructions,
                schema.Fields,
                groundingContext,
                ct);
            return result;
        }
        finally
        {
            var modelId = string.IsNullOrWhiteSpace(result?.ModelId)
                ? "claude-cli"
                : result.ModelId;
            var latency = (int)Math.Clamp(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                0,
                int.MaxValue);
            await usageRecorder.RecordUsageAsync(
                portfolioId,
                "claude-cli",
                modelId,
                feature,
                latency,
                Math.Max(0, result?.InputTokens ?? 0),
                Math.Max(0, result?.OutputTokens ?? 0),
                0m,
                ct.IsCancellationRequested ? CancellationToken.None : ct,
                usageEventIdentity);
        }
    }

    private static async Task<ExtractedFields> InvokeWorkspaceExtractionAsync(
        IWorkspaceLlmExtractionProvider provider,
        WorkspaceLlmRuntimeCredential credential,
        ILlmUsageEvidenceRecorder usageRecorder,
        int portfolioId,
        string feature,
        string usageEventIdentity,
        byte[] bytes,
        string contentType,
        ExtractionSchema schema,
        string? groundingContext,
        CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        ExtractedFields? result = null;
        try
        {
            result = await provider.ExtractWorkspaceAsync(
                credential,
                bytes,
                contentType,
                schema.Instructions,
                schema.Fields,
                groundingContext,
                ct);
            return result;
        }
        finally
        {
            var modelId = string.IsNullOrWhiteSpace(result?.ModelId)
                ? credential.ModelId
                : result.ModelId;
            var inputUnits = Math.Max(0, result?.InputTokens ?? 0);
            var outputUnits = Math.Max(0, result?.OutputTokens ?? 0);
            var latency = (int)Math.Clamp(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                0,
                int.MaxValue);
            await usageRecorder.RecordUsageAsync(
                portfolioId,
                provider.ProviderKey,
                modelId,
                feature,
                latency,
                inputUnits,
                outputUnits,
                EstimateCost(modelId, inputUnits, outputUnits),
                ct.IsCancellationRequested ? CancellationToken.None : ct,
                usageEventIdentity);
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
