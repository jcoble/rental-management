using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Imaging;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Implements <see cref="IScanService"/>: stores upload, creates a <see cref="ScanDraft"/>,
/// and handles confirm (→Expense) and reject lifecycle transitions.
/// </summary>
public sealed class ScanService : IScanService
{
    private readonly RentalCommandDbContext _db;
    private readonly IScanFileService _files;
    private readonly IExpenseService _expenses;
    private readonly IPaymentService _payments;
    private readonly IWorkOrderService _workOrders;
    private readonly ILeaseService _leases;
    private readonly ITenantService _tenants;
    private readonly IPropertyService _properties;
    private readonly IUnitService _units;
    private readonly IApplicationService _applications;
    private readonly IAuditTrailService _audit;
    private readonly ILogger<ScanService> _logger;

    public ScanService(
        RentalCommandDbContext db,
        IScanFileService files,
        IExpenseService expenses,
        IPaymentService payments,
        IWorkOrderService workOrders,
        ILeaseService leases,
        ITenantService tenants,
        IPropertyService properties,
        IUnitService units,
        IApplicationService applications,
        IAuditTrailService audit,
        ILogger<ScanService> logger)
    {
        _db = db;
        _files = files;
        _expenses = expenses;
        _payments = payments;
        _workOrders = workOrders;
        _leases = leases;
        _tenants = tenants;
        _properties = properties;
        _units = units;
        _applications = applications;
        _audit = audit;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // CreateDraftAsync
    // -------------------------------------------------------------------------

    public Task<ScanDraft> CreateDraftAsync(
        int portfolioId,
        byte[] fileBytes,
        string contentType,
        string targetEntityType,
        CancellationToken ct = default)
        => CreateDraftCoreAsync(portfolioId, batchId: null, fileBytes, contentType, targetEntityType, ct);

    public Task<ScanDraft> CreateBatchDraftAsync(
        int portfolioId,
        int batchId,
        byte[] fileBytes,
        string contentType,
        string targetEntityType,
        CancellationToken ct = default)
        => CreateDraftCoreAsync(portfolioId, batchId, fileBytes, contentType, targetEntityType, ct);

    /// <summary>
    /// Shared store-file → preview → persist-draft path for both single-file and batch uploads.
    /// <paramref name="batchId"/> links the draft into a bulk-scan batch when non-null.
    /// </summary>
    private async Task<ScanDraft> CreateDraftCoreAsync(
        int portfolioId,
        int? batchId,
        byte[] fileBytes,
        string contentType,
        string targetEntityType,
        CancellationToken ct)
    {
        var stored = await _files.StoreAsync(
            portfolioId,
            targetEntityType,
            fileBytes,
            $"scan-{DateTime.UtcNow:yyyyMMddHHmmss}",
            contentType,
            ct);

        // Generate a small JPEG preview so clients (especially mobile) never fetch the
        // full-resolution original just to render the review thumbnail. Best-effort:
        // ResizeToJpeg returns null for non-images (e.g. PDFs); the file endpoint then
        // falls back to serving the original.
        string? thumbnailPath = null;
        var thumbBytes = ThumbnailResizer.ResizeToJpeg(fileBytes, maxDim: 1000, quality: 72);
        if (thumbBytes is not null)
        {
            var thumbStored = await _files.StoreAsync(
                portfolioId,
                targetEntityType,
                thumbBytes,
                $"scan-thumb-{DateTime.UtcNow:yyyyMMddHHmmss}",
                "image/jpeg",
                ct);
            thumbnailPath = thumbStored.FilePath;
        }

        var draft = new ScanDraft
        {
            PortfolioId = portfolioId,
            BatchId = batchId,
            FilePath = stored.FilePath,
            ThumbnailPath = thumbnailPath,
            TargetEntityType = targetEntityType,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        };

        _db.ScanDrafts.Add(draft);
        await _db.SaveChangesAsync(ct);
        return draft;
    }

    // -------------------------------------------------------------------------
    // ConfirmAndCreateAsync
    // -------------------------------------------------------------------------

    public async Task<ScanConfirmResult> ConfirmAndCreateAsync(
        int portfolioId,
        int draftId,
        int userId,
        string overridesJson,
        CancellationToken ct = default)
    {
        var draft = await _db.ScanDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == draftId && d.PortfolioId == portfolioId, ct);

        if (draft is null)
            return new ScanConfirmResult(false, null, "Draft not found");

        if (draft.Status is "Confirmed" or "Rejected")
            return new ScanConfirmResult(false, null, $"Draft is already {draft.Status.ToLowerInvariant()}");

        // Confirm is only valid once extraction has finished and the draft is awaiting review.
        // Pending/Processing/Failed (and any other state) must be rejected even if a target was
        // chosen at upload time — confirming a not-yet-reviewed draft would create a record from
        // unreviewed (or absent) extraction data.
        if (draft.Status != "Reviewing")
            return new ScanConfirmResult(false, null, "Draft is not ready to confirm; it must be reviewed first.");

        if (draft.TargetEntityType is not ("Expense" or "Payment" or "WorkOrder" or "Lease" or "Application"))
            return new ScanConfirmResult(false, null, $"Unsupported target '{draft.TargetEntityType}'");

        // Start from the extracted fields, then apply the user's reviewed overrides (overrides win).
        var dto = BuildReceiptDto(draft.ExtractedFields);
        ApplyOverrides(dto, overridesJson);

        // Wrap the WHOLE confirm-and-create operation in a single transaction so it is all-or-nothing:
        //   1. claim the draft (Reviewing → Confirming)
        //   2. create the Expense/Payment
        //   3. re-key the StoredFile and mark the draft Confirmed
        // Any failure (exception, cancellation, or a service returning null) rolls the transaction back,
        // leaving NO orphaned entity and the draft restored to its prior "Reviewing" status — never stuck
        // in "Confirming". The conditional claim still prevents concurrent double-confirms: PostgreSQL row-
        // locks the claimed draft for the transaction's lifetime, so a second confirm blocks then sees the
        // committed "Confirmed" (or rolled-back "Reviewing") status.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Atomically claim the draft so two concurrent confirms can't both create an entity.
        // The conditional UPDATE only matches a not-yet-finalized, not-in-flight draft; the DB
        // serializes concurrent callers so exactly one wins (affected == 1).
        var claimed = await _db.ScanDrafts
            .Where(d => d.Id == draftId && d.PortfolioId == portfolioId
                && d.Status != "Confirmed" && d.Status != "Rejected" && d.Status != "Confirming")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "Confirming"), ct);

        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return new ScanConfirmResult(false, null, "Draft is already being confirmed or finalized");
        }

        // ---- ROUTER ----
        var result = draft.TargetEntityType switch
        {
            "Payment" => await ConfirmAsPaymentAsync(portfolioId, draftId, userId, draft, dto, overridesJson, ct),
            "WorkOrder" => await ConfirmAsWorkOrderAsync(portfolioId, draftId, userId, draft, overridesJson, ct),
            "Lease" => await ConfirmAsLeaseAsync(portfolioId, draftId, userId, draft, overridesJson, ct),
            "Application" => await ConfirmAsApplicationAsync(portfolioId, draftId, userId, draft, overridesJson, ct),
            _ => await ConfirmAsExpenseAsync(portfolioId, draftId, userId, draft, dto, overridesJson, ct),
        };

        if (!result.Success)
        {
            // The entity work (or a guard) failed — discard the claim and everything else atomically.
            await tx.RollbackAsync(ct);
            return result;
        }

        await tx.CommitAsync(ct);
        return result;
    }

    // -------------------------------------------------------------------------
    // BuildLeaseProposalAsync  — read-only "what confirm will do" for the review UI
    // -------------------------------------------------------------------------

    public async Task<LeaseImportProposal?> BuildLeaseProposalAsync(
        int portfolioId,
        int draftId,
        string overridesJson,
        CancellationToken ct = default)
    {
        var draft = await _db.ScanDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == draftId && d.PortfolioId == portfolioId, ct);

        // Only lease drafts have a property/unit proposal; everything else returns null (no preview).
        if (draft is null || draft.TargetEntityType is not "Lease")
            return null;

        // Same field-building → IDOR validation → overrides chain the confirm path uses, so the preview
        // reflects exactly what confirm would do. Writes nothing.
        var fields = BuildLeaseFields(draft.ExtractedFields);
        await ValidateLeaseIdsInPortfolioAsync(portfolioId, fields, ct);
        ApplyLeaseOverrides(fields, overridesJson);

        // ---- Property proposal ----
        ProposedRecord propertyProposal;
        int? resolvedPropertyId = null;
        if (fields.PropertyId > 0 &&
            await _db.EnsurePropertyInPortfolioAsync(portfolioId, fields.PropertyId, ct))
        {
            // A grounded/override id that checks out → link the existing property.
            var existing = await _db.Properties.AsNoTracking()
                .Where(p => p.Id == fields.PropertyId)
                .Select(p => new { p.Name, p.AddressLine1, p.City })
                .FirstOrDefaultAsync(ct);
            resolvedPropertyId = fields.PropertyId;
            propertyProposal = new ProposedRecord("link", fields.PropertyId,
                existing?.Name, FormatAddress(existing?.AddressLine1, existing?.City));
        }
        else
        {
            var match = await FindMatchingPropertyAsync(portfolioId, fields, ct);
            if (match is not null)
            {
                resolvedPropertyId = match.Id;
                propertyProposal = new ProposedRecord("link", match.Id, match.Label, match.Detail);
            }
            else if (!string.IsNullOrWhiteSpace(fields.PropertyAddress) || !string.IsNullOrWhiteSpace(fields.PropertyName))
            {
                // Nothing matched but we have enough to create one.
                var label = !string.IsNullOrWhiteSpace(fields.PropertyName)
                    ? fields.PropertyName!.Trim()
                    : fields.PropertyAddress!.Trim();
                propertyProposal = new ProposedRecord("create", null, label,
                    FormatAddress(fields.PropertyAddress, fields.PropertyCity));
            }
            else
            {
                // No id, no address, no name — the reviewer must choose a property.
                propertyProposal = new ProposedRecord("select", null, null, null);
            }
        }

        // ---- Unit proposal (scoped to the resolved property when there is one) ----
        ProposedRecord unitProposal;
        var unitNumber = DefaultUnitNumber(fields.UnitNumber);
        if (fields.UnitId is > 0 &&
            await _db.EnsureUnitInPortfolioAsync(portfolioId, fields.UnitId.Value,
                resolvedPropertyId, ct))
        {
            var existingUnit = await _db.Units.AsNoTracking()
                .Where(u => u.Id == fields.UnitId.Value)
                .Select(u => u.UnitNumber)
                .FirstOrDefaultAsync(ct);
            unitProposal = new ProposedRecord("link", fields.UnitId.Value,
                $"Unit {existingUnit}", null);
        }
        else if (resolvedPropertyId is int propId)
        {
            var unitMatch = await FindMatchingUnitAsync(propId, unitNumber, ct);
            unitProposal = unitMatch is not null
                ? new ProposedRecord("link", unitMatch.Id, $"Unit {unitMatch.Label}", null)
                : new ProposedRecord("create", null, $"Unit {unitNumber}", null);
        }
        else
        {
            // Property itself is unresolved, so the unit will be created under whatever property the
            // reviewer ends up with — describe it as a create with the (defaulted) unit number.
            unitProposal = new ProposedRecord("create", null, $"Unit {unitNumber}", null);
        }

        return new LeaseImportProposal(propertyProposal, unitProposal);
    }

    // -------------------------------------------------------------------------
    // ConfirmAsExpenseAsync  (called by the router)
    // -------------------------------------------------------------------------

    private async Task<ScanConfirmResult> ConfirmAsExpenseAsync(
        int portfolioId,
        int draftId,
        int userId,
        ScanDraft draft,
        ExtractedReceiptDto dto,
        string overridesJson,
        CancellationToken ct)
    {
        // Determine paid vs unpaid, and pick up an optional property selection.
        // The review UI may send is_paid explicitly; if absent, derive from document_kind.
        // It may also send propertyId so the expense is filed under a property; when absent
        // the expense is left unlinked (we never fabricate a property).
        bool isPaid;
        int? propertyId = null;
        try
        {
            using var overrideDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(overridesJson) ? "{}" : overridesJson);
            var overrideRoot = overrideDoc.RootElement;
            if (overrideRoot.TryGetProperty("is_paid", out var isPaidEl) &&
                (isPaidEl.ValueKind == JsonValueKind.True || isPaidEl.ValueKind == JsonValueKind.False))
            {
                isPaid = isPaidEl.GetBoolean();
            }
            else
            {
                isPaid = dto.DocumentKind is null or "Receipt" or "Other"
                    ? true
                    : dto.DocumentKind is "Bill" or "Invoice" or "UtilityBill" or "PropertyTax"
                        ? false
                        : true; // fallback to paid for unknown kinds
            }

            if (TryGetOverrideInt(overrideRoot, out var pid, "propertyId", "property_id") && pid > 0)
                propertyId = pid;
        }
        catch
        {
            isPaid = dto.DocumentKind is null or "Receipt" or "Other"
                ? true
                : dto.DocumentKind is "Bill" or "Invoice" or "UtilityBill" or "PropertyTax"
                    ? false
                    : true;
        }

        // Build ReceiptData JSON for the non-promoted details.
        var receiptDataJson = BuildReceiptDataJson(dto);

        var expenseAmount = dto.Total ?? dto.Subtotal ?? 0m;
        if (expenseAmount <= 0m)
        {
            // Transaction rollback in the caller releases the "Confirming" claim.
            return new ScanConfirmResult(false, null, "Confirmed amount must be greater than zero.");
        }

        var request = new CreateExpenseRequest
        {
            PropertyId  = propertyId, // null when no property context; ExpenseService validates in-portfolio.
            Category    = dto.Category ?? ScheduleECategory.Other,
            Description = string.IsNullOrWhiteSpace(dto.VendorName) ? "Scanned receipt" : dto.VendorName!,
            Amount      = expenseAmount,
            Subtotal    = dto.Subtotal,
            TaxAmount   = dto.Tax,
            IncurredAt  = dto.TransactionDate ?? DateTime.UtcNow,
            BillableToOwner = false,
            Notes       = dto.Notes,
            ReceiptData = receiptDataJson,
            // Promote high-value scalars to typed columns (the ReceiptData jsonb keeps the full superset).
            PaymentMethod = dto.PaymentMethod,
            CardLast4     = dto.CardLast4,
            DocumentKind  = dto.DocumentKind,
            // Promote line items to queryable child rows; 1-based LineNumber preserves the printed order.
            LineItems = dto.LineItems
                .Select((li, i) => new CreateExpenseLineItem
                {
                    Description = li.Description ?? string.Empty,
                    Quantity    = li.Quantity,
                    UnitPrice   = li.UnitPrice,
                    Amount      = li.Amount,
                    LineNumber  = i + 1,
                })
                .ToList(),
        };

        if (isPaid)
        {
            request.Status  = ExpenseStatus.Paid;
            request.PaidAt  = dto.TransactionDate ?? DateTime.UtcNow;
            request.DueDate = null;
        }
        else
        {
            request.Status  = ExpenseStatus.Pending;
            request.DueDate = dto.DueDate;
            request.PaidAt  = null;
        }

        ExpenseResponse? expense;
        try
        {
            expense = await _expenses.CreateAsync(portfolioId, request, ct);
        }
        catch (DomainValidationException ex)
        {
            _logger.LogInformation(ex, "Expense creation was rejected while confirming scan draft {DraftId}", draftId);
            return new ScanConfirmResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Expense creation threw while confirming scan draft {DraftId}", draftId);
            expense = null;
        }

        if (expense is null)
        {
            // Null means the entity service rejected the request (e.g. a property not in this
            // portfolio) or failed; the caller's transaction rollback releases the claim.
            return new ScanConfirmResult(false, null, "Expense creation failed");
        }

        await FinalizeDraft(portfolioId, draftId, userId, draft.FilePath, "Expense", expense.Id, ct);

        var appliedJson = JsonSerializer.Serialize(new
        {
            vendorName      = dto.VendorName,
            total           = dto.Total,
            subtotal        = dto.Subtotal,
            tax             = dto.Tax,
            transactionDate = dto.TransactionDate,
            category        = dto.Category?.ToString(),
            notes           = dto.Notes,
            lineItemCount   = dto.LineItems.Count,
        });

        await _audit.LogAsync(
            portfolioId,
            "Expense",
            expense.Id,
            AuditLogOperation.Created,
            userId: userId,
            oldValues: draft.ExtractedFields,
            newValues: appliedJson,
            changeReason: "Created from scan draft #" + draftId,
            ct: ct);

        return new ScanConfirmResult(true, expense.Id, null, "Expense");
    }

    // -------------------------------------------------------------------------
    // ConfirmAsPaymentAsync  (called by the router)
    // -------------------------------------------------------------------------

    private async Task<ScanConfirmResult> ConfirmAsPaymentAsync(
        int portfolioId,
        int draftId,
        int userId,
        ScanDraft draft,
        ExtractedReceiptDto dto,
        string overridesJson,
        CancellationToken ct)
    {
        // The review UI must supply a leaseId for payment routing.
        int leaseId = 0;
        try
        {
            using var overrideDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(overridesJson) ? "{}" : overridesJson);
            var overrideRoot = overrideDoc.RootElement;
            if (TryGetOverrideInt(overrideRoot, out var lid, "leaseId", "lease_id"))
                leaseId = lid;
        }
        catch { /* leave leaseId == 0 */ }

        if (leaseId <= 0)
        {
            // Transaction rollback in the caller releases the "Confirming" claim.
            return new ScanConfirmResult(false, null, "Select a lease for this payment");
        }

        var paymentAmount = dto.Total ?? dto.Subtotal ?? 0m;
        if (paymentAmount <= 0m)
        {
            // Transaction rollback in the caller releases the "Confirming" claim.
            return new ScanConfirmResult(false, null, "Confirmed amount must be greater than zero.");
        }

        // Build notes from payer name + any free-text notes on the document.
        var notes = string.Join(" — ",
            new[] { dto.PayerName, dto.Notes }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        var paymentDate = dto.TransactionDate ?? DateTime.UtcNow;

        var paymentRequest = new CreatePaymentRequest
        {
            LeaseId           = leaseId,
            PaymentType       = PaymentType.Rent,
            Status            = PaymentStatus.Paid,
            Amount            = paymentAmount,
            DueDate           = paymentDate,
            PaidDate          = paymentDate,
            Method            = "Check",
            ExternalReference = dto.CheckNumber,
            Notes             = string.IsNullOrWhiteSpace(notes) ? null : notes,
            // Promote scanned check fields to typed columns; keep the full extraction as the jsonb extras.
            PayerName         = dto.PayerName,
            CheckNumber       = dto.CheckNumber,
            BankName          = dto.BankName,
            ExtractedData     = NormalizeExtractedData(draft.ExtractedFields),
        };

        PaymentResponse? payment;
        try
        {
            payment = await _payments.CreateAsync(portfolioId, paymentRequest, ct);
        }
        catch (DomainValidationException ex)
        {
            _logger.LogInformation(ex, "Payment creation was rejected while confirming scan draft {DraftId}", draftId);
            return new ScanConfirmResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payment creation threw while confirming scan draft {DraftId}", draftId);
            payment = null;
        }

        if (payment is null)
        {
            // Null means the entity service rejected the request (e.g. a lease not in this
            // portfolio) or failed; the caller's transaction rollback releases the claim.
            return new ScanConfirmResult(false, null, "Payment creation failed");
        }

        await FinalizeDraft(portfolioId, draftId, userId, draft.FilePath, "Payment", payment.Id, ct);

        var appliedJson = JsonSerializer.Serialize(new
        {
            payerName         = dto.PayerName,
            checkNumber       = dto.CheckNumber,
            bankName          = dto.BankName,
            amount            = dto.Total ?? dto.Subtotal,
            transactionDate   = dto.TransactionDate,
            leaseId,
        });

        await _audit.LogAsync(
            portfolioId,
            "Payment",
            payment.Id,
            AuditLogOperation.Created,
            userId: userId,
            oldValues: draft.ExtractedFields,
            newValues: appliedJson,
            changeReason: "Created from scan draft #" + draftId,
            ct: ct);

        return new ScanConfirmResult(true, payment.Id, null, "Payment");
    }

    // -------------------------------------------------------------------------
    // ConfirmAsWorkOrderAsync  (called by the router)
    // -------------------------------------------------------------------------

    private async Task<ScanConfirmResult> ConfirmAsWorkOrderAsync(
        int portfolioId,
        int draftId,
        int userId,
        ScanDraft draft,
        string overridesJson,
        CancellationToken ct)
    {
        var fields = BuildWorkOrderFields(draft.ExtractedFields);

        // The id fields above came straight from the LLM. Before we trust them for auto-fill, drop any
        // that aren't actually in THIS portfolio: a hallucinated or foreign id must never pre-select
        // another portfolio's row (IDOR), and a bogus id would otherwise make WorkOrderService.CreateAsync
        // reject the whole confirm. Foreign/unknown ids fall back to 0/null so the landlord picks manually.
        await ValidateWorkOrderIdsInPortfolioAsync(portfolioId, fields, ct);

        // Trusted user selections from the review UI win and are re-validated in-portfolio by CreateAsync.
        ApplyWorkOrderOverrides(fields, overridesJson);

        if (fields.PropertyId <= 0)
            return new ScanConfirmResult(false, null, "Select a property for this work order");

        if (string.IsNullOrWhiteSpace(fields.Title))
            return new ScanConfirmResult(false, null, "Work order title is required");

        if (string.IsNullOrWhiteSpace(fields.Description))
            return new ScanConfirmResult(false, null, "Work order description is required");

        var request = new CreateWorkOrderRequest
        {
            PropertyId = fields.PropertyId,
            UnitId = fields.UnitId,
            TenantId = fields.TenantId,
            LeaseId = fields.LeaseId,
            VendorId = fields.VendorId,
            Title = fields.Title!,
            Description = fields.Description!,
            Category = string.IsNullOrWhiteSpace(fields.Category) ? "General" : fields.Category!,
            Priority = fields.Priority,
            Status = WorkOrderStatus.New,
            RequestedAt = DateTime.UtcNow,
            EstimatedCost = fields.EstimatedCost,
            CreatedBy = userId.ToString(),
            // Keep the full scan extraction superset as jsonb extras.
            ExtractedData = NormalizeExtractedData(draft.ExtractedFields),
        };

        WorkOrderResponse? workOrder;
        try
        {
            workOrder = await _workOrders.CreateAsync(portfolioId, request, userId, "Staff", ct);
        }
        catch (DomainValidationException ex)
        {
            _logger.LogInformation(ex, "Work order creation was rejected while confirming scan draft {DraftId}", draftId);
            return new ScanConfirmResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Work order creation threw while confirming scan draft {DraftId}", draftId);
            workOrder = null;
        }

        if (workOrder is null)
            return new ScanConfirmResult(false, null, "Work order creation failed");

        await FinalizeDraft(portfolioId, draftId, userId, draft.FilePath, "WorkOrder", workOrder.Id, ct);

        var appliedJson = JsonSerializer.Serialize(new
        {
            request.PropertyId,
            request.UnitId,
            request.TenantId,
            request.Title,
            request.Description,
            request.Category,
            Priority = request.Priority.ToString(),
            request.EstimatedCost,
        });

        await _audit.LogAsync(
            portfolioId,
            "WorkOrder",
            workOrder.Id,
            AuditLogOperation.Created,
            userId: userId,
            oldValues: draft.ExtractedFields,
            newValues: appliedJson,
            changeReason: "Created from scan draft #" + draftId,
            ct: ct);

        return new ScanConfirmResult(true, workOrder.Id, null, "WorkOrder");
    }

    // -------------------------------------------------------------------------
    // ConfirmAsLeaseAsync  (called by the router) — the "import your PDF leases" path
    // -------------------------------------------------------------------------

    private async Task<ScanConfirmResult> ConfirmAsLeaseAsync(
        int portfolioId,
        int draftId,
        int userId,
        ScanDraft draft,
        string overridesJson,
        CancellationToken ct)
    {
        var fields = BuildLeaseFields(draft.ExtractedFields);

        // The property/unit/tenant ids above came straight from the LLM. Drop any that aren't actually
        // in THIS portfolio before we trust them: a hallucinated or foreign id must never link another
        // portfolio's row (IDOR). Foreign/unknown ids fall back to 0/null so the reviewer picks manually.
        await ValidateLeaseIdsInPortfolioAsync(portfolioId, fields, ct);

        // Trusted user selections from the review UI win and are re-validated in-portfolio below.
        ApplyLeaseOverrides(fields, overridesJson);

        // Resolve the property: a validated grounded/override id wins; otherwise match the extracted
        // leased-premises address to an existing in-portfolio property, and if none matches, CREATE
        // it. This is the empty-portfolio bootstrap — a brand-new landlord scans a stack of leases and
        // the properties are created from the documents (no "the property must already exist" wall).
        int propertyId;
        if (fields.PropertyId > 0)
        {
            // Re-validate the (possibly override-supplied) id — never trust a raw override id either.
            if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, fields.PropertyId, ct))
                return new ScanConfirmResult(false, null, "Selected property is not in this portfolio");
            propertyId = fields.PropertyId;
        }
        else
        {
            var resolvedProperty = await ResolveOrCreatePropertyAsync(portfolioId, fields, ct);
            if (resolvedProperty is null)
                return new ScanConfirmResult(false, null,
                    "Select a property for this lease, or provide the property address so it can be created");
            propertyId = resolvedProperty.Value;
        }

        // Resolve the unit UNDER the resolved property: a validated grounded/override id wins (and must
        // belong to that property); otherwise match the extracted unit number under the property, and if
        // none matches, CREATE it. A single-family lease with no unit designation still gets a unit row
        // (a sensible default unit number) so every lease has the property→unit→lease spine.
        int unitId;
        if (fields.UnitId is > 0)
        {
            if (!await _db.EnsureUnitInPortfolioAsync(portfolioId, fields.UnitId.Value, propertyId, ct))
                return new ScanConfirmResult(false, null, "Selected unit is not in this portfolio");
            unitId = fields.UnitId.Value;
        }
        else
        {
            var resolvedUnit = await ResolveOrCreateUnitAsync(portfolioId, propertyId, fields, ct);
            if (resolvedUnit is null)
                return new ScanConfirmResult(false, null, "Unit creation failed for this lease");
            unitId = resolvedUnit.Value;
        }

        // Resolve the tenant: an override-supplied tenantId wins (validated in-portfolio); otherwise, if
        // the extracted tenant_name doesn't match an existing tenant, chain a new in-portfolio Tenant from
        // the name. An imported lease's tenant is frequently not yet on file, so creating one keeps the
        // "the computer does the typing" promise instead of dead-ending the import.
        int tenantId;
        if (fields.TenantId is > 0)
        {
            if (!await _db.EnsureTenantInPortfolioAsync(portfolioId, fields.TenantId.Value, ct))
                return new ScanConfirmResult(false, null, "Selected tenant is not in this portfolio");
            tenantId = fields.TenantId.Value;
        }
        else
        {
            var resolved = await ResolveOrCreateTenantAsync(portfolioId, fields.TenantName, ct);
            if (resolved is null)
                return new ScanConfirmResult(false, null, "Select a tenant for this lease");
            tenantId = resolved.Value;
        }

        if (fields.StartDate is null || fields.EndDate is null)
            return new ScanConfirmResult(false, null, "Lease start and end dates are required");

        if (fields.MonthlyRent is not > 0m)
            return new ScanConfirmResult(false, null, "Monthly rent must be greater than zero");

        var leaseNumber = string.IsNullOrWhiteSpace(fields.LeaseNumber)
            ? $"SCAN-{DateTime.UtcNow:yyyyMMddHHmmss}"
            : fields.LeaseNumber!;

        var request = new CreateLeaseRequest
        {
            PropertyId      = propertyId,
            UnitId          = unitId,
            TenantId        = tenantId,
            LeaseNumber     = leaseNumber,
            Status          = LeaseStatus.Active,
            StartDate       = fields.StartDate.Value,
            EndDate         = fields.EndDate.Value,
            MonthlyRent     = fields.MonthlyRent.Value,
            SecurityDeposit = fields.SecurityDeposit ?? 0m,
            LateFeeAmount   = fields.LateFee ?? 0m,
            RentDueDay      = fields.RentDueDay is >= 1 and <= 31 ? fields.RentDueDay.Value : 1,
            Notes           = "Imported from scanned lease PDF.",
            // Keep the full scan extraction superset as jsonb extras.
            ExtractedData   = NormalizeExtractedData(draft.ExtractedFields),
        };

        LeaseResponse? lease;
        try
        {
            lease = await _leases.CreateAsync(portfolioId, request, ct);
        }
        catch (DomainValidationException ex)
        {
            _logger.LogInformation(ex, "Lease creation was rejected while confirming scan draft {DraftId}", draftId);
            return new ScanConfirmResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lease creation threw while confirming scan draft {DraftId}", draftId);
            lease = null;
        }

        if (lease is null)
            return new ScanConfirmResult(false, null, "Lease creation failed");

        // Re-attach the source PDF to the created lease (FinalizeDraft re-keys the StoredFile + marks Confirmed).
        await FinalizeDraft(portfolioId, draftId, userId, draft.FilePath, "Lease", lease.Id, ct);

        var appliedJson = JsonSerializer.Serialize(new
        {
            request.PropertyId,
            request.UnitId,
            request.TenantId,
            request.LeaseNumber,
            Status = request.Status.ToString(),
            request.StartDate,
            request.EndDate,
            request.MonthlyRent,
            request.SecurityDeposit,
            request.LateFeeAmount,
            request.RentDueDay,
        });

        await _audit.LogAsync(
            portfolioId,
            "Lease",
            lease.Id,
            AuditLogOperation.Created,
            userId: userId,
            oldValues: draft.ExtractedFields,
            newValues: appliedJson,
            changeReason: "Created from scan draft #" + draftId,
            ct: ct);

        return new ScanConfirmResult(true, lease.Id, null, "Lease");
    }

    // -------------------------------------------------------------------------
    // ConfirmAsApplicationAsync  (called by the router) — the "scan a completed paper application" path
    // -------------------------------------------------------------------------

    private async Task<ScanConfirmResult> ConfirmAsApplicationAsync(
        int portfolioId,
        int draftId,
        int userId,
        ScanDraft draft,
        string overridesJson,
        CancellationToken ct)
    {
        var fields = BuildApplicationFields(draft.ExtractedFields);

        // The property/unit ids came straight from the LLM. Drop any that aren't actually in THIS portfolio
        // before we trust them (IDOR) — same pattern as the lease/work-order paths. Trusted reviewer
        // selections from the review UI then win and are re-validated below.
        await ValidateApplicationIdsInPortfolioAsync(portfolioId, fields, ct);
        ApplyApplicationOverrides(fields, overridesJson);

        // First + last name anchor the applicant record (both are [Required] on the create request).
        if (string.IsNullOrWhiteSpace(fields.FirstName) || string.IsNullOrWhiteSpace(fields.LastName))
            return new ScanConfirmResult(false, null, "Applicant first and last name are required");

        // Re-validate any reviewer-supplied property/unit id in-portfolio (never trust a raw override id).
        int? propertyId = null;
        if (fields.PropertyId is > 0)
        {
            if (await _db.EnsurePropertyInPortfolioAsync(portfolioId, fields.PropertyId.Value, ct))
                propertyId = fields.PropertyId;
        }
        int? unitId = null;
        if (fields.UnitId is > 0)
        {
            if (await _db.EnsureUnitInPortfolioAsync(portfolioId, fields.UnitId.Value, propertyId, ct))
                unitId = fields.UnitId;
        }

        // Fold the application-only details that have no dedicated RentalApplication column (the applied-for
        // property/unit text, the ID last-4 hint, and any co-signer) into the Notes so they aren't lost. The
        // structured property/unit selection (above) is the queryable link; this is the human-readable record.
        var notes = BuildApplicationNotes(fields);

        var request = new CreateApplicationRequest
        {
            PropertyId = propertyId,
            UnitId = unitId,
            FirstName = fields.FirstName!.Trim(),
            LastName = fields.LastName!.Trim(),
            Email = fields.Email,
            Phone = fields.Phone,
            DateOfBirth = fields.DateOfBirth,
            CurrentAddress = fields.CurrentAddress,
            Employer = fields.Employer,
            MonthlyIncome = fields.MonthlyIncome,
            DesiredMoveInDate = fields.DesiredMoveInDate,
            Notes = notes,
            // Keep the full scan extraction superset as the provenance JSON.
            IdExtractedFields = NormalizeExtractedData(draft.ExtractedFields),
        };

        ApplicationResponse application;
        try
        {
            application = await _applications.CreateFromScanAsync(portfolioId, request, userId, ct);
        }
        catch (DomainValidationException ex)
        {
            _logger.LogInformation(ex, "Application creation was rejected while confirming scan draft {DraftId}", draftId);
            return new ScanConfirmResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Application creation threw while confirming scan draft {DraftId}", draftId);
            return new ScanConfirmResult(false, null, "Application creation failed");
        }

        // Re-attach the source document to the created application (re-key the StoredFile + mark Confirmed).
        await FinalizeDraft(portfolioId, draftId, userId, draft.FilePath, "Application", application.Id, ct);

        var appliedJson = JsonSerializer.Serialize(new
        {
            request.FirstName,
            request.LastName,
            request.Email,
            request.Phone,
            request.DateOfBirth,
            request.Employer,
            request.MonthlyIncome,
            request.PropertyId,
            request.UnitId,
        });

        await _audit.LogAsync(
            portfolioId,
            "RentalApplication",
            application.Id,
            AuditLogOperation.Created,
            userId: userId,
            oldValues: draft.ExtractedFields,
            newValues: appliedJson,
            changeReason: "Created from scan draft #" + draftId,
            ct: ct);

        return new ScanConfirmResult(true, application.Id, null, "Application");
    }

    /// <summary>
    /// Folds the scanned-application details that have no dedicated <see cref="Core.Entities.RentalApplication"/>
    /// column — the applied-for property/unit free text, the government-ID last-4 hint, and any co-signer name —
    /// into the application Notes, after any free-text notes the reviewer entered, so nothing the scan captured
    /// is silently dropped. Returns null when there is nothing to record.
    /// </summary>
    private static string? BuildApplicationNotes(ApplicationDraftFields fields)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(fields.Notes))
            parts.Add(fields.Notes!.Trim());
        if (!string.IsNullOrWhiteSpace(fields.ApplyingFor))
            parts.Add($"Applying for: {fields.ApplyingFor!.Trim()}.");
        if (!string.IsNullOrWhiteSpace(fields.IdLast4))
            parts.Add($"ID last-4: {fields.IdLast4!.Trim()}.");
        if (!string.IsNullOrWhiteSpace(fields.CoSignerName))
            parts.Add($"Co-signer: {fields.CoSignerName!.Trim()}.");

        if (parts.Count == 0)
            return null;
        var note = string.Join(" ", parts);
        return note.Length > 2000 ? note[..2000] : note;
    }

    /// <summary>
    /// Resolves the tenant for an imported lease from the extracted name: returns an existing tenant's id
    /// when the name matches (case-insensitive on the combined first+last), otherwise creates a new
    /// in-portfolio Tenant from the name ("chained Tenant") and returns its id. Returns null only when
    /// there is no usable name to create from.
    /// </summary>
    private async Task<int?> ResolveOrCreateTenantAsync(int portfolioId, string? tenantName, CancellationToken ct)
    {
        var name = tenantName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Match against existing in-portfolio tenants by full name (case-insensitive). Whitespace-collapse
        // both sides so "Jane   Doe" still matches "Jane Doe".
        var normalized = CollapseWhitespace(name);
        var existing = await _db.Tenants
            .Where(t => t.PortfolioId == portfolioId && t.DeletedAt == null)
            .Select(t => new { t.Id, FullName = (t.FirstName + " " + t.LastName).Trim() })
            .ToListAsync(ct);

        var match = existing.FirstOrDefault(t =>
            string.Equals(CollapseWhitespace(t.FullName), normalized, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match.Id;

        // No match — chain a new Tenant. Split the name into first/last on the last space.
        var (firstName, lastName) = SplitName(name);
        var created = await _tenants.CreateAsync(
            portfolioId,
            new CreateTenantRequest
            {
                FirstName = firstName,
                LastName = lastName,
                Notes = "Created from scanned lease PDF.",
            },
            ct);

        return created.Id;
    }

    /// <summary>
    /// Resolves the property for an imported lease from the extracted leased-premises address: returns
    /// an existing in-portfolio property's id when its address matches (normalized AddressLine1 + City,
    /// or property name as a fallback), otherwise CREATES a new property from the extracted address and
    /// returns its id. This is the empty-portfolio bootstrap — properties are created from the documents.
    /// Returns null only when there is no usable address or name to match/create from (so the reviewer
    /// must supply one). Matching is the dedupe guard: re-scanning a lease for an existing property links
    /// rather than duplicates.
    /// </summary>
    private async Task<int?> ResolveOrCreatePropertyAsync(
        int portfolioId, LeaseDraftFields fields, CancellationToken ct)
    {
        var address = fields.PropertyAddress?.Trim();
        var name = fields.PropertyName?.Trim();

        // Nothing to anchor on — neither a street address nor a building name. The reviewer must pick.
        if (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(name))
            return null;

        // Reuse the same match key the proposal preview uses, so "what confirm will do" never drifts
        // from what confirm actually does.
        var match = await FindMatchingPropertyAsync(portfolioId, fields, ct);
        if (match is not null)
            return match.Id;

        // No match — create the property from the extracted premises. Required-ish address parts that the
        // lease omitted get a clear placeholder so the row is obviously "needs review" rather than blank.
        var created = await _properties.CreateAsync(
            portfolioId,
            new CreatePropertyRequest
            {
                Name = !string.IsNullOrWhiteSpace(name)
                    ? name!
                    : !string.IsNullOrWhiteSpace(address) ? address! : "Imported Property",
                // Honor the reviewer's chosen property type; fall back to the request default
                // (MultiFamily) when absent or an unrecognized value, rather than crashing.
                PropertyType = Enum.TryParse<PropertyType>(fields.PropertyType, ignoreCase: true, out var parsedType)
                    ? parsedType
                    : PropertyType.MultiFamily,
                AddressLine1 = !string.IsNullOrWhiteSpace(address) ? address! : "Unknown",
                City = !string.IsNullOrWhiteSpace(fields.PropertyCity) ? fields.PropertyCity!.Trim() : "Unknown",
                State = !string.IsNullOrWhiteSpace(fields.PropertyState) ? fields.PropertyState!.Trim() : "Unknown",
                PostalCode = !string.IsNullOrWhiteSpace(fields.PropertyPostalCode) ? fields.PropertyPostalCode!.Trim() : "Unknown",
                Notes = "Created from scanned lease PDF.",
            },
            ct);

        // CreateAsync only returns null on a cross-tenant owner reference, which this path never sets.
        return created?.Id;
    }

    /// <summary>
    /// Resolves the unit for an imported lease UNDER the already-resolved property: returns an existing
    /// unit's id when its unit number matches (case-insensitive) within that property, otherwise CREATES
    /// a new unit and returns its id. A lease with no unit designation (single-family home) uses a default
    /// unit number so every lease still has a property→unit→lease spine, and re-scanning that lease matches
    /// the same default unit rather than duplicating it. Returns null only if unit creation fails.
    /// </summary>
    private async Task<int?> ResolveOrCreateUnitAsync(
        int portfolioId, int propertyId, LeaseDraftFields fields, CancellationToken ct)
    {
        // Default a missing unit number so a single-family lease still produces a unit (and re-scans dedupe).
        var unitNumber = DefaultUnitNumber(fields.UnitNumber);

        var match = await FindMatchingUnitAsync(propertyId, unitNumber, ct);
        if (match is not null)
            return match.Id;

        var created = await _units.CreateAsync(
            portfolioId,
            new CreateUnitRequest
            {
                PropertyId = propertyId,
                UnitNumber = unitNumber,
                Bedrooms = fields.UnitBedrooms is >= 0 and <= 99 ? fields.UnitBedrooms.Value : 0m,
                Bathrooms = fields.UnitBathrooms is >= 0 and <= 99 ? fields.UnitBathrooms.Value : 0m,
                SquareFeet = fields.UnitSquareFeet is > 0 ? fields.UnitSquareFeet : null,
                MarketRent = fields.MonthlyRent is > 0m ? fields.MonthlyRent.Value : 0m,
                Status = UnitStatus.Occupied, // an imported lease means the unit is currently leased
                Notes = "Created from scanned lease PDF.",
            },
            ct);

        // CreateAsync returns null only when the property is missing/out of scope — we just resolved it
        // in-portfolio above, so this is a real failure (don't silently swallow it into a foreign link).
        return created?.Id;
    }

    /// <summary>A property the lease's extracted address/name matched in this portfolio, or null.</summary>
    private sealed record PropertyMatch(int Id, string Label, string? Detail);

    /// <summary>A unit the lease's extracted unit number matched under the property, or null.</summary>
    private sealed record UnitMatch(int Id, string Label);

    /// <summary>
    /// Finds the in-portfolio property the lease's extracted leased-premises address/name dedupes to,
    /// or null when none matches. Primary key is the normalized street address (+ city when both sides
    /// specify one); falls back to a building/community name match when no street address was extracted.
    /// Shared by the confirm path and the read-only proposal preview so the two never diverge.
    /// </summary>
    private async Task<PropertyMatch?> FindMatchingPropertyAsync(
        int portfolioId, LeaseDraftFields fields, CancellationToken ct)
    {
        var addressKey = NormalizeAddress(fields.PropertyAddress);
        var nameKey = CollapseWhitespace(fields.PropertyName ?? string.Empty).ToLowerInvariant();
        var cityKey = CollapseWhitespace(fields.PropertyCity ?? string.Empty).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(addressKey) && nameKey.Length == 0)
            return null;

        // Pull the portfolio's active properties and match in memory: address normalization (suffix
        // abbreviations, punctuation, case) isn't expressible in a translatable EF query.
        var candidates = await _db.Properties
            .Where(p => p.PortfolioId == portfolioId && p.DeletedAt == null)
            .Select(p => new { p.Id, p.Name, p.AddressLine1, p.City })
            .ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(addressKey))
        {
            var byAddress = candidates.FirstOrDefault(p =>
            {
                var pAddr = NormalizeAddress(p.AddressLine1);
                if (string.IsNullOrWhiteSpace(pAddr) || pAddr != addressKey)
                    return false;
                var pCity = CollapseWhitespace(p.City ?? string.Empty).ToLowerInvariant();
                // Only require city agreement when BOTH sides actually specify one.
                return string.IsNullOrWhiteSpace(cityKey) || string.IsNullOrWhiteSpace(pCity) || pCity == cityKey;
            });
            if (byAddress is not null)
                return new PropertyMatch(byAddress.Id, byAddress.Name, FormatAddress(byAddress.AddressLine1, byAddress.City));
        }
        else
        {
            var byName = candidates.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.Name) &&
                CollapseWhitespace(p.Name).ToLowerInvariant() == nameKey);
            if (byName is not null)
                return new PropertyMatch(byName.Id, byName.Name, FormatAddress(byName.AddressLine1, byName.City));
        }

        return null;
    }

    /// <summary>
    /// Finds the in-portfolio unit with the given unit number under the property, or null. Shared by the
    /// confirm path and the proposal preview.
    /// </summary>
    private async Task<UnitMatch?> FindMatchingUnitAsync(int propertyId, string unitNumber, CancellationToken ct)
    {
        var unitKey = CollapseWhitespace(unitNumber).ToLowerInvariant();

        var existing = await _db.Units
            .Where(u => u.PropertyId == propertyId && u.DeletedAt == null)
            .Select(u => new { u.Id, u.UnitNumber })
            .ToListAsync(ct);

        var match = existing.FirstOrDefault(u =>
            CollapseWhitespace(u.UnitNumber ?? string.Empty).ToLowerInvariant() == unitKey);
        return match is null ? null : new UnitMatch(match.Id, match.UnitNumber);
    }

    /// <summary>The unit number to use, defaulting a blank one to "1" so single-family leases still get a unit.</summary>
    private static string DefaultUnitNumber(string? unitNumber) =>
        string.IsNullOrWhiteSpace(unitNumber) ? "1" : unitNumber!.Trim();

    private static string FormatAddress(string? line1, string? city)
    {
        var parts = new[] { line1?.Trim(), city?.Trim() }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Normalizes a US street address for dedupe matching: lowercase, strip punctuation, collapse
    /// whitespace, and fold the most common street-suffix abbreviations so "123 Maple St" and
    /// "123 Maple Street" compare equal. Best-effort — not a full address parser; it just makes the
    /// match-or-create dedupe forgiving of the formatting noise typical of scanned leases.
    /// </summary>
    private static string NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var lowered = value.ToLowerInvariant();
        var cleaned = new System.Text.StringBuilder(lowered.Length);
        foreach (var ch in lowered)
            cleaned.Append(char.IsLetterOrDigit(ch) ? ch : ' ');

        var tokens = cleaned.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (StreetSuffixAbbreviations.TryGetValue(tokens[i], out var canonical))
                tokens[i] = canonical;
        }
        return string.Join(' ', tokens);
    }

    // Common US street-suffix variants → a single canonical token, so address dedupe doesn't break on
    // "St" vs "Street". Bounded, well-known set; not an attempt at a full USPS suffix table.
    private static readonly Dictionary<string, string> StreetSuffixAbbreviations =
        new(StringComparer.Ordinal)
        {
            ["st"] = "street", ["str"] = "street", ["street"] = "street",
            ["ave"] = "avenue", ["av"] = "avenue", ["avenue"] = "avenue",
            ["rd"] = "road", ["road"] = "road",
            ["dr"] = "drive", ["drive"] = "drive",
            ["ln"] = "lane", ["lane"] = "lane",
            ["ct"] = "court", ["court"] = "court",
            ["blvd"] = "boulevard", ["boulevard"] = "boulevard",
            ["pl"] = "place", ["place"] = "place",
            ["cir"] = "circle", ["circle"] = "circle",
            ["ter"] = "terrace", ["terrace"] = "terrace",
            ["pkwy"] = "parkway", ["parkway"] = "parkway",
            ["hwy"] = "highway", ["highway"] = "highway",
            ["way"] = "way",
            ["apt"] = "apt", ["unit"] = "unit", ["ste"] = "suite", ["suite"] = "suite",
            ["n"] = "north", ["s"] = "south", ["e"] = "east", ["w"] = "west",
        };

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static (string FirstName, string LastName) SplitName(string fullName)
    {
        var parts = fullName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return ("Tenant", "(scanned)");
        if (parts.Length == 1)
            return (parts[0], "(scanned)"); // LastName is [Required] on the create request — never empty.
        return (string.Join(' ', parts[..^1]), parts[^1]);
    }

    // -------------------------------------------------------------------------
    // Shared finalize helpers
    // -------------------------------------------------------------------------

    // NOTE: there is no longer a ReleaseClaim helper. The whole confirm-and-create runs inside
    // a single transaction (see ConfirmAndCreateAsync); any failure path returns a non-Success
    // result and the caller rolls the transaction back, which atomically restores the draft's
    // prior "Reviewing" status. Writing "Reviewing" by hand here would be redundant — and risky
    // if it ever ran outside the transaction.

    /// <summary>
    /// Re-keys the StoredFile to the newly created entity, then marks the draft Confirmed.
    /// Called by both Expense and Payment branches after successful entity creation,
    /// inside the confirm transaction so the file re-key and the status flip commit together.
    /// </summary>
    private async Task FinalizeDraft(
        int portfolioId,
        int draftId,
        int userId,
        string filePath,
        string entityType,
        int entityId,
        CancellationToken ct)
    {
        await _db.StoredFiles
            .Where(f => f.PortfolioId == portfolioId && f.FilePath == filePath)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.EntityType, entityType)
                .SetProperty(f => f.EntityId, (int?)entityId), ct);

        await _db.ScanDrafts
            .Where(d => d.Id == draftId && d.PortfolioId == portfolioId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, "Confirmed")
                .SetProperty(d => d.ConfirmedAt, (DateTime?)DateTime.UtcNow)
                .SetProperty(d => d.ReviewedBy, userId.ToString()), ct);
    }

    // -------------------------------------------------------------------------
    // RejectDraftAsync
    // -------------------------------------------------------------------------

    public async Task<bool> RejectDraftAsync(
        int portfolioId,
        int draftId,
        int userId,
        string? reason,
        CancellationToken ct = default)
    {
        var draft = await _db.ScanDrafts
            .FirstOrDefaultAsync(d => d.Id == draftId && d.PortfolioId == portfolioId, ct);

        if (draft is null)
            return false;

        if (draft.Status is "Confirmed" or "Rejected" or "Confirming")
            return false; // already finalized or mid-confirm — don't race with a concurrent confirm

        draft.Status = "Rejected";
        draft.ReviewedAt = DateTime.UtcNow;
        draft.ReviewedBy = userId.ToString();
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            portfolioId,
            "ScanDraft",
            draftId,
            AuditLogOperation.Rejected,
            userId: userId,
            changeReason: reason,
            ct: ct);

        return true;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Deserializes the worker-written JSON shape
    /// <c>{"vendor_name":{"value":"...","confidence":0.9}, "total":{...}, "line_items":{"value":"[...]","confidence":0.9}, ...}</c>
    /// into an <see cref="ExtractedReceiptDto"/>. Never throws on null/empty/malformed JSON.
    /// </summary>
    private ExtractedReceiptDto BuildReceiptDto(string? extractedFieldsJson)
    {
        var dto = new ExtractedReceiptDto();

        if (string.IsNullOrWhiteSpace(extractedFieldsJson))
            return dto;

        try
        {
            using var doc = JsonDocument.Parse(extractedFieldsJson);
            var root = doc.RootElement;

            // ---- Vendor ----
            dto.VendorName    = ReadFieldValue(root, "vendor_name");
            dto.VendorAddress = ReadFieldValue(root, "vendor_address");
            dto.VendorPhone   = ReadFieldValue(root, "vendor_phone");
            dto.VendorWebsite = ReadFieldValue(root, "vendor_website");
            dto.VendorTaxId   = ReadFieldValue(root, "vendor_tax_id");

            // ---- Receipt header ----
            dto.ReceiptNumber = ReadFieldValue(root, "receipt_number");

            var dateStr = ReadFieldValue(root, "transaction_date");
            if (!string.IsNullOrWhiteSpace(dateStr) &&
                DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            {
                dto.TransactionDate = date;
            }

            // ---- Money breakdown ----
            dto.Subtotal = ParseDecimalField(root, "subtotal");
            dto.Tax      = ParseDecimalField(root, "tax");
            dto.TaxRate  = ParseDecimalField(root, "tax_rate");
            dto.Tip      = ParseDecimalField(root, "tip");
            dto.Discount = ParseDecimalField(root, "discount");
            dto.Shipping = ParseDecimalField(root, "shipping");
            // Accept "amount" as an alias for "total" — single-amount documents (rent checks,
            // simple receipts) naturally use "amount"; "total" wins when both are present.
            dto.Total    = ParseDecimalField(root, "total") ?? ParseDecimalField(root, "amount");

            // ---- Payment ----
            dto.PaymentMethod = ReadFieldValue(root, "payment_method");
            dto.CardLast4     = ReadFieldValue(root, "card_last4");

            // ---- Classification ----
            var categoryStr = ReadFieldValue(root, "category");
            if (!string.IsNullOrWhiteSpace(categoryStr) &&
                Enum.TryParse<ScheduleECategory>(categoryStr, ignoreCase: true, out var category))
            {
                dto.Category = category;
            }

            dto.DocumentKind = ReadFieldValue(root, "document_kind");

            var dueDateStr = ReadFieldValue(root, "due_date");
            if (!string.IsNullOrWhiteSpace(dueDateStr) &&
                DateTime.TryParse(dueDateStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var dueDate))
            {
                dto.DueDate = dueDate;
            }

            dto.Notes = ReadFieldValue(root, "notes");

            // ---- Rent check fields ----
            dto.PayerName   = ReadFieldValue(root, "payer_name");
            dto.CheckNumber = ReadFieldValue(root, "check_number");
            dto.BankName    = ReadFieldValue(root, "bank_name");

            // ---- Line items ----
            // line_items is stored as {"value":"[...]","confidence":0.9} where value is a JSON array string.
            dto.LineItems = ParseLineItems(root, "line_items");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse extractedFieldsJson; returning partial ReceiptDto.");
        }

        return dto;
    }

    /// <summary>Parses a decimal from a field's "value" sub-property. Returns null on any failure.</summary>
    private static decimal? ParseDecimalField(JsonElement root, string key)
    {
        var str = ReadFieldValue(root, key);
        if (string.IsNullOrWhiteSpace(str)) return null;
        return decimal.TryParse(str, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Parses the line_items array stored as a JSON string in the extraction JSON.
    /// Tolerates missing or malformed data, always returns a list (never throws).
    /// </summary>
    private List<ReceiptLineItem> ParseLineItems(JsonElement root, string key)
    {
        var result = new List<ReceiptLineItem>();
        try
        {
            if (!root.TryGetProperty(key, out var fieldEl))
                return result;

            // The value may be a JSON array string (stored by the parser), or a nested object.
            string? arrayJson = null;
            if (fieldEl.ValueKind == JsonValueKind.Object &&
                fieldEl.TryGetProperty("value", out var valueEl))
            {
                arrayJson = valueEl.ValueKind == JsonValueKind.String
                    ? valueEl.GetString()
                    : valueEl.ValueKind == JsonValueKind.Array
                        ? valueEl.GetRawText()
                        : null;
            }
            else if (fieldEl.ValueKind == JsonValueKind.Array)
            {
                arrayJson = fieldEl.GetRawText();
            }

            if (string.IsNullOrWhiteSpace(arrayJson))
                return result;

            using var arrDoc = JsonDocument.Parse(arrayJson);
            if (arrDoc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in arrDoc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string? desc = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() : null;

                decimal? qty = null, unitPrice = null, amount = null;
                if (item.TryGetProperty("quantity", out var qEl) && qEl.ValueKind == JsonValueKind.Number)
                    qEl.TryGetDecimal(out var q); // assignment handled below to avoid CS0165

                // Re-parse each numeric property safely.
                qty       = TryGetItemDecimal(item, "quantity");
                unitPrice = TryGetItemDecimal(item, "unit_price");
                amount    = TryGetItemDecimal(item, "amount");

                result.Add(new ReceiptLineItem(desc, qty, unitPrice, amount));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse line_items from extraction JSON; ignoring.");
        }
        return result;
    }

    private static decimal? TryGetItemDecimal(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var v)) return v;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var vs)) return vs;
        return null;
    }

    /// <summary>
    /// Builds the ReceiptData JSON string that captures non-promoted details from the extraction DTO.
    /// </summary>
    private static string BuildReceiptDataJson(ExtractedReceiptDto dto)
    {
        try
        {
            var lineItems = dto.LineItems.Select(li => new
            {
                description = li.Description,
                quantity    = li.Quantity,
                unitPrice   = li.UnitPrice,
                amount      = li.Amount,
            }).ToArray();

            var obj = new
            {
                documentKind  = dto.DocumentKind,
                dueDate       = dto.DueDate,
                vendor = new
                {
                    address = dto.VendorAddress,
                    phone   = dto.VendorPhone,
                    website = dto.VendorWebsite,
                    taxId   = dto.VendorTaxId,
                },
                receiptNumber = dto.ReceiptNumber,
                paymentMethod = dto.PaymentMethod,
                cardLast4     = dto.CardLast4,
                taxRate       = dto.TaxRate,
                tip           = dto.Tip,
                discount      = dto.Discount,
                shipping      = dto.Shipping,
                lineItems,
                extra         = dto.Extra,
            };

            return JsonSerializer.Serialize(obj);
        }
        catch
        {
            return "{}";
        }
    }

    /// <summary>
    /// Returns the raw worker-written extraction JSON as the jsonb "extras" superset for
    /// Payment/Lease/WorkOrder, but only when it parses as valid JSON — a jsonb column rejects
    /// malformed text, so anything unparseable (or empty) becomes null rather than failing the save.
    /// </summary>
    private static string? NormalizeExtractedData(string? extractedFieldsJson)
    {
        if (string.IsNullOrWhiteSpace(extractedFieldsJson))
            return null;
        try
        {
            using var _ = JsonDocument.Parse(extractedFieldsJson);
            return extractedFieldsJson;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the <c>value</c> property from a nested field object, e.g.
    /// <c>{"vendor_name": {"value": "ACME", "confidence": 0.9}}</c>.
    /// Returns null if the key is absent or the shape doesn't match.
    /// </summary>
    private static string? ReadFieldValue(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var fieldEl))
            return null;

        if (fieldEl.ValueKind == JsonValueKind.Object &&
            fieldEl.TryGetProperty("value", out var valueEl) &&
            valueEl.ValueKind == JsonValueKind.String)
        {
            return valueEl.GetString();
        }

        // Tolerate a plain string value at the top level.
        if (fieldEl.ValueKind == JsonValueKind.String)
            return fieldEl.GetString();

        return null;
    }

    /// <summary>
    /// Merges a flat override JSON into the DTO, overwriting any present key.
    /// Accepts both camelCase (web) and snake_case (field names) keys.
    /// Never throws on null/empty/malformed JSON. A present "line_items" array replaces the
    /// extracted line items wholesale (the reviewer can edit/add/remove rows); an absent key
    /// leaves them untouched.
    /// </summary>
    private void ApplyOverrides(ExtractedReceiptDto dto, string overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(overridesJson);
            var root = doc.RootElement;

            // Accept BOTH the documented camelCase override schema AND the snake_case field
            // names the review UI keys edits by (vendor_name/transaction_date). The web maps to
            // camelCase, but tolerating both here guarantees a corrected vendor/date is never
            // silently dropped on confirm — a trust-critical guarantee for the review gate.
            if (TryGetOverrideString(root, out var vendor, "vendorName", "vendor_name"))
                dto.VendorName = vendor;

            if (TryGetOverrideString(root, out var vendorAddress, "vendorAddress", "vendor_address"))
                dto.VendorAddress = vendorAddress;

            if (TryGetOverrideString(root, out var vendorPhone, "vendorPhone", "vendor_phone"))
                dto.VendorPhone = vendorPhone;

            if (TryGetOverrideString(root, out var vendorWebsite, "vendorWebsite", "vendor_website"))
                dto.VendorWebsite = vendorWebsite;

            if (TryGetOverrideString(root, out var receiptNumber, "receiptNumber", "receipt_number"))
                dto.ReceiptNumber = receiptNumber;

            if (TryGetOverrideString(root, out var dateStr, "transactionDate", "transaction_date") &&
                DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                dto.TransactionDate = parsedDate;
            }

            if (TryGetOverrideDecimal(root, out var subtotal, "subtotal"))
                dto.Subtotal = subtotal;

            if (TryGetOverrideDecimal(root, out var tax, "tax"))
                dto.Tax = tax;

            if (TryGetOverrideDecimal(root, out var total, "total", "amount"))
                dto.Total = total;

            if (TryGetOverrideString(root, out var paymentMethod, "paymentMethod", "payment_method"))
                dto.PaymentMethod = paymentMethod;

            if (TryGetOverrideString(root, out var notes, "notes"))
                dto.Notes = notes;

            if (TryGetOverrideString(root, out var catStr, "category") &&
                Enum.TryParse<ScheduleECategory>(catStr, ignoreCase: true, out var parsedCat))
            {
                dto.Category = parsedCat;
            }

            if (TryGetOverrideString(root, out var dueDateStr2, "dueDate", "due_date") &&
                DateTime.TryParse(dueDateStr2, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var overrideDueDate))
            {
                dto.DueDate = overrideDueDate;
            }

            if (TryGetOverrideString(root, out var documentKind, "documentKind", "document_kind"))
                dto.DocumentKind = documentKind;

            // ---- Rent check fields ----
            if (TryGetOverrideString(root, out var payerName, "payerName", "payer_name"))
                dto.PayerName = payerName;

            if (TryGetOverrideString(root, out var checkNumber, "checkNumber", "check_number"))
                dto.CheckNumber = checkNumber;

            if (TryGetOverrideString(root, out var bankName, "bankName", "bank_name"))
                dto.BankName = bankName;

            // ---- Line items ----
            // The review UI sends the full edited line-items array under "line_items" once the
            // reviewer touches the table (a real JSON array of {description, quantity, unit_price,
            // amount}). Presence of the key — even an empty array — means "use exactly these", so a
            // deleted row stays deleted and a cleared table persists no items; absence leaves the
            // extracted items untouched (scalar-only edits don't disturb them). Reuses the same
            // tolerant per-item parser as extraction, so a bad numeric edit can never throw here.
            if (root.TryGetProperty("line_items", out _))
                dto.LineItems = ParseLineItems(root, "line_items");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse overridesJson; skipping overrides.");
        }
    }

    private WorkOrderDraftFields BuildWorkOrderFields(string? extractedFieldsJson)
    {
        var fields = new WorkOrderDraftFields();
        if (string.IsNullOrWhiteSpace(extractedFieldsJson))
            return fields;

        try
        {
            using var doc = JsonDocument.Parse(extractedFieldsJson);
            var root = doc.RootElement;

            fields.PropertyId = ParseIntField(root, "property_id") ?? ParseIntField(root, "propertyId") ?? 0;
            fields.UnitId = ParseIntField(root, "unit_id") ?? ParseIntField(root, "unitId");
            fields.TenantId = ParseIntField(root, "tenant_id") ?? ParseIntField(root, "tenantId");
            fields.LeaseId = ParseIntField(root, "lease_id") ?? ParseIntField(root, "leaseId");
            fields.VendorId = ParseIntField(root, "vendor_id") ?? ParseIntField(root, "vendorId");
            fields.Title = ReadFieldValue(root, "title");
            fields.Description = ReadFieldValue(root, "description") ?? ReadFieldValue(root, "transcript");
            fields.Category = ReadFieldValue(root, "category");
            fields.EstimatedCost = ParseDecimalField(root, "estimated_cost") ?? ParseDecimalField(root, "estimatedCost");

            var priority = ReadFieldValue(root, "priority");
            if (!string.IsNullOrWhiteSpace(priority) &&
                Enum.TryParse<WorkOrderPriority>(priority, ignoreCase: true, out var parsedPriority))
            {
                fields.Priority = parsedPriority;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse work-order extraction JSON.");
        }

        return fields;
    }

    /// <summary>
    /// Verifies each LLM-suggested id (property/unit/tenant/lease/vendor) actually belongs to this
    /// portfolio and clears any that don't. The model is told to copy ids from the grounded list, but
    /// it can still hallucinate or echo a foreign id, so we re-check against the live in-portfolio rows
    /// here (the grounding set is itself a portfolio-scoped query, so the DB is the authoritative check).
    /// Unit must additionally belong to the matched property. This keeps auto-fill IDOR-safe and stops a
    /// single bad id from failing the whole confirm — bad ids fall back to 0/null for manual selection.
    /// </summary>
    private async Task ValidateWorkOrderIdsInPortfolioAsync(
        int portfolioId, WorkOrderDraftFields fields, CancellationToken ct)
    {
        if (fields.PropertyId > 0 &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, fields.PropertyId, ct))
        {
            // Property is the anchor: if it's not ours, the dependent unit can't be trusted either.
            fields.PropertyId = 0;
        }

        if (fields.UnitId is > 0 &&
            !await _db.EnsureUnitInPortfolioAsync(
                portfolioId, fields.UnitId.Value, fields.PropertyId > 0 ? fields.PropertyId : null, ct))
        {
            fields.UnitId = null;
        }

        if (fields.TenantId is > 0 &&
            !await _db.EnsureTenantInPortfolioAsync(portfolioId, fields.TenantId.Value, ct))
        {
            fields.TenantId = null;
        }

        if (fields.LeaseId is > 0 &&
            !await _db.EnsureLeaseInPortfolioAsync(portfolioId, fields.LeaseId.Value, ct))
        {
            fields.LeaseId = null;
        }

        if (fields.VendorId is > 0 &&
            !await _db.EnsureVendorInPortfolioAsync(portfolioId, fields.VendorId.Value, ct))
        {
            fields.VendorId = null;
        }
    }

    private void ApplyWorkOrderOverrides(WorkOrderDraftFields fields, string overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(overridesJson);
            var root = doc.RootElement;

            if (TryGetOverrideInt(root, out var propertyId, "propertyId", "property_id"))
                fields.PropertyId = propertyId;
            if (TryGetOverrideInt(root, out var unitId, "unitId", "unit_id"))
                fields.UnitId = unitId > 0 ? unitId : null;
            if (TryGetOverrideInt(root, out var tenantId, "tenantId", "tenant_id"))
                fields.TenantId = tenantId > 0 ? tenantId : null;
            if (TryGetOverrideInt(root, out var leaseId, "leaseId", "lease_id"))
                fields.LeaseId = leaseId > 0 ? leaseId : null;
            if (TryGetOverrideInt(root, out var vendorId, "vendorId", "vendor_id"))
                fields.VendorId = vendorId > 0 ? vendorId : null;
            if (TryGetOverrideString(root, out var title, "title"))
                fields.Title = title;
            if (TryGetOverrideString(root, out var description, "description"))
                fields.Description = description;
            if (TryGetOverrideString(root, out var category, "category"))
                fields.Category = category;
            if (TryGetOverrideDecimal(root, out var estimatedCost, "estimatedCost", "estimated_cost"))
                fields.EstimatedCost = estimatedCost;
            if (TryGetOverrideString(root, out var priority, "priority") &&
                Enum.TryParse<WorkOrderPriority>(priority, ignoreCase: true, out var parsedPriority))
            {
                fields.Priority = parsedPriority;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse work-order overridesJson; skipping overrides.");
        }
    }

    // -------------------------------------------------------------------------
    // Lease extraction helpers
    // -------------------------------------------------------------------------

    private LeaseDraftFields BuildLeaseFields(string? extractedFieldsJson)
    {
        var fields = new LeaseDraftFields();
        if (string.IsNullOrWhiteSpace(extractedFieldsJson))
            return fields;

        try
        {
            using var doc = JsonDocument.Parse(extractedFieldsJson);
            var root = doc.RootElement;

            fields.PropertyId = ParseIntField(root, "property_id") ?? ParseIntField(root, "propertyId") ?? 0;
            fields.UnitId = ParseIntField(root, "unit_id") ?? ParseIntField(root, "unitId");
            fields.TenantId = ParseIntField(root, "tenant_id") ?? ParseIntField(root, "tenantId");
            fields.TenantName = ReadFieldValue(root, "tenant_name") ?? ReadFieldValue(root, "tenantName");
            fields.PropertyName = ReadFieldValue(root, "property_name") ?? ReadFieldValue(root, "propertyName");
            fields.PropertyAddress = ReadFieldValue(root, "property_address") ?? ReadFieldValue(root, "propertyAddress");
            fields.PropertyCity = ReadFieldValue(root, "property_city") ?? ReadFieldValue(root, "propertyCity");
            fields.PropertyState = ReadFieldValue(root, "property_state") ?? ReadFieldValue(root, "propertyState");
            fields.PropertyPostalCode = ReadFieldValue(root, "property_postal_code") ?? ReadFieldValue(root, "propertyPostalCode");
            fields.UnitNumber = ReadFieldValue(root, "unit_number") ?? ReadFieldValue(root, "unitNumber");
            fields.UnitBedrooms = ParseDecimalField(root, "unit_bedrooms") ?? ParseDecimalField(root, "unitBedrooms");
            fields.UnitBathrooms = ParseDecimalField(root, "unit_bathrooms") ?? ParseDecimalField(root, "unitBathrooms");
            fields.UnitSquareFeet = ParseIntField(root, "unit_square_feet") ?? ParseIntField(root, "unitSquareFeet");
            fields.LeaseNumber = ReadFieldValue(root, "lease_number") ?? ReadFieldValue(root, "leaseNumber");
            fields.StartDate = ParseDateField(root, "start_date") ?? ParseDateField(root, "startDate");
            fields.EndDate = ParseDateField(root, "end_date") ?? ParseDateField(root, "endDate");
            fields.MonthlyRent = ParseDecimalField(root, "monthly_rent") ?? ParseDecimalField(root, "monthlyRent");
            fields.SecurityDeposit = ParseDecimalField(root, "security_deposit") ?? ParseDecimalField(root, "securityDeposit");
            fields.LateFee = ParseDecimalField(root, "late_fee") ?? ParseDecimalField(root, "lateFee");
            fields.RentDueDay = ParseIntField(root, "rent_due_day") ?? ParseIntField(root, "rentDueDay");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse lease extraction JSON.");
        }

        return fields;
    }

    /// <summary>
    /// Verifies each LLM-suggested id (property/unit/tenant) belongs to this portfolio and clears any that
    /// don't — same IDOR-safe pattern as the work-order path. Unit must additionally belong to the matched
    /// property. Bad ids fall back to 0/null for manual selection rather than failing the whole confirm.
    /// </summary>
    private async Task ValidateLeaseIdsInPortfolioAsync(
        int portfolioId, LeaseDraftFields fields, CancellationToken ct)
    {
        if (fields.PropertyId > 0 &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, fields.PropertyId, ct))
        {
            fields.PropertyId = 0;
        }

        if (fields.UnitId is > 0 &&
            !await _db.EnsureUnitInPortfolioAsync(
                portfolioId, fields.UnitId.Value, fields.PropertyId > 0 ? fields.PropertyId : null, ct))
        {
            fields.UnitId = null;
        }

        if (fields.TenantId is > 0 &&
            !await _db.EnsureTenantInPortfolioAsync(portfolioId, fields.TenantId.Value, ct))
        {
            fields.TenantId = null;
        }
    }

    private void ApplyLeaseOverrides(LeaseDraftFields fields, string overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(overridesJson);
            var root = doc.RootElement;

            if (TryGetOverrideInt(root, out var propertyId, "propertyId", "property_id"))
                fields.PropertyId = propertyId;
            if (TryGetOverrideInt(root, out var unitId, "unitId", "unit_id"))
                fields.UnitId = unitId > 0 ? unitId : null;
            if (TryGetOverrideInt(root, out var tenantId, "tenantId", "tenant_id"))
                fields.TenantId = tenantId > 0 ? tenantId : null;
            if (TryGetOverrideString(root, out var tenantName, "tenantName", "tenant_name"))
                fields.TenantName = tenantName;
            // Leased-premises corrections: the reviewer can fix the address/unit the match-or-create
            // uses. Supplying propertyId=0 (above) forces "create new" using these fields even if the
            // model had guessed an id.
            if (TryGetOverrideString(root, out var propertyName, "propertyName", "property_name"))
                fields.PropertyName = propertyName;
            if (TryGetOverrideString(root, out var propertyType, "propertyType", "property_type"))
                fields.PropertyType = propertyType;
            if (TryGetOverrideString(root, out var propertyAddress, "propertyAddress", "property_address"))
                fields.PropertyAddress = propertyAddress;
            if (TryGetOverrideString(root, out var propertyCity, "propertyCity", "property_city"))
                fields.PropertyCity = propertyCity;
            if (TryGetOverrideString(root, out var propertyState, "propertyState", "property_state"))
                fields.PropertyState = propertyState;
            if (TryGetOverrideString(root, out var propertyPostal, "propertyPostalCode", "property_postal_code"))
                fields.PropertyPostalCode = propertyPostal;
            if (TryGetOverrideString(root, out var unitNumber, "unitNumber", "unit_number"))
                fields.UnitNumber = unitNumber;
            if (TryGetOverrideDecimal(root, out var unitBeds, "unitBedrooms", "unit_bedrooms"))
                fields.UnitBedrooms = unitBeds;
            if (TryGetOverrideDecimal(root, out var unitBaths, "unitBathrooms", "unit_bathrooms"))
                fields.UnitBathrooms = unitBaths;
            if (TryGetOverrideInt(root, out var unitSqft, "unitSquareFeet", "unit_square_feet"))
                fields.UnitSquareFeet = unitSqft > 0 ? unitSqft : null;
            if (TryGetOverrideString(root, out var leaseNumber, "leaseNumber", "lease_number"))
                fields.LeaseNumber = leaseNumber;
            if (TryGetOverrideString(root, out var startStr, "startDate", "start_date") &&
                DateTime.TryParse(startStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var start))
            {
                fields.StartDate = start;
            }
            if (TryGetOverrideString(root, out var endStr, "endDate", "end_date") &&
                DateTime.TryParse(endStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var end))
            {
                fields.EndDate = end;
            }
            if (TryGetOverrideDecimal(root, out var rent, "monthlyRent", "monthly_rent"))
                fields.MonthlyRent = rent;
            if (TryGetOverrideDecimal(root, out var deposit, "securityDeposit", "security_deposit"))
                fields.SecurityDeposit = deposit;
            if (TryGetOverrideDecimal(root, out var lateFee, "lateFee", "late_fee"))
                fields.LateFee = lateFee;
            if (TryGetOverrideInt(root, out var dueDay, "rentDueDay", "rent_due_day"))
                fields.RentDueDay = dueDay;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse lease overridesJson; skipping overrides.");
        }
    }

    // -------------------------------------------------------------------------
    // Application extraction helpers (scan a completed paper rental application)
    // -------------------------------------------------------------------------

    private ApplicationDraftFields BuildApplicationFields(string? extractedFieldsJson)
    {
        var fields = new ApplicationDraftFields();
        if (string.IsNullOrWhiteSpace(extractedFieldsJson))
            return fields;

        try
        {
            using var doc = JsonDocument.Parse(extractedFieldsJson);
            var root = doc.RootElement;

            fields.FirstName = ReadFieldValue(root, "first_name") ?? ReadFieldValue(root, "firstName");
            fields.LastName = ReadFieldValue(root, "last_name") ?? ReadFieldValue(root, "lastName");
            fields.Email = ReadFieldValue(root, "email");
            fields.Phone = ReadFieldValue(root, "phone");
            fields.DateOfBirth = ParseDateField(root, "date_of_birth") ?? ParseDateField(root, "dateOfBirth");
            fields.CurrentAddress = ReadFieldValue(root, "current_address") ?? ReadFieldValue(root, "currentAddress");
            fields.Employer = ReadFieldValue(root, "employer");
            fields.MonthlyIncome = ParseDecimalField(root, "monthly_income") ?? ParseDecimalField(root, "monthlyIncome");
            fields.ApplyingFor = ReadFieldValue(root, "applying_for") ?? ReadFieldValue(root, "applyingFor");
            fields.DesiredMoveInDate = ParseDateField(root, "desired_move_in_date") ?? ParseDateField(root, "desiredMoveInDate");
            fields.IdLast4 = ReadFieldValue(root, "id_last4") ?? ReadFieldValue(root, "idLast4");
            fields.CoSignerName = ReadFieldValue(root, "co_signer_name") ?? ReadFieldValue(root, "coSignerName");
            fields.PropertyId = ParseIntField(root, "property_id") ?? ParseIntField(root, "propertyId");
            fields.UnitId = ParseIntField(root, "unit_id") ?? ParseIntField(root, "unitId");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse application extraction JSON.");
        }

        return fields;
    }

    /// <summary>
    /// Drops any LLM-suggested property/unit id that doesn't belong to this portfolio (IDOR-safe) — same
    /// pattern as the lease/work-order paths. Unit must additionally belong to the matched property. Bad ids
    /// fall back to null so the applicant is simply filed without a property rather than failing the confirm.
    /// </summary>
    private async Task ValidateApplicationIdsInPortfolioAsync(
        int portfolioId, ApplicationDraftFields fields, CancellationToken ct)
    {
        if (fields.PropertyId is > 0 &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, fields.PropertyId.Value, ct))
        {
            fields.PropertyId = null;
        }

        if (fields.UnitId is > 0 &&
            !await _db.EnsureUnitInPortfolioAsync(
                portfolioId, fields.UnitId.Value, fields.PropertyId is > 0 ? fields.PropertyId : null, ct))
        {
            fields.UnitId = null;
        }
    }

    private void ApplyApplicationOverrides(ApplicationDraftFields fields, string overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(overridesJson);
            var root = doc.RootElement;

            if (TryGetOverrideString(root, out var firstName, "firstName", "first_name"))
                fields.FirstName = firstName;
            if (TryGetOverrideString(root, out var lastName, "lastName", "last_name"))
                fields.LastName = lastName;
            if (TryGetOverrideString(root, out var email, "email"))
                fields.Email = email;
            if (TryGetOverrideString(root, out var phone, "phone"))
                fields.Phone = phone;
            if (TryGetOverrideString(root, out var dobStr, "dateOfBirth", "date_of_birth") &&
                DateTime.TryParse(dobStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var dob))
            {
                fields.DateOfBirth = dob;
            }
            if (TryGetOverrideString(root, out var currentAddress, "currentAddress", "current_address"))
                fields.CurrentAddress = currentAddress;
            if (TryGetOverrideString(root, out var employer, "employer"))
                fields.Employer = employer;
            if (TryGetOverrideDecimal(root, out var income, "monthlyIncome", "monthly_income"))
                fields.MonthlyIncome = income;
            if (TryGetOverrideString(root, out var applyingFor, "applyingFor", "applying_for"))
                fields.ApplyingFor = applyingFor;
            if (TryGetOverrideString(root, out var moveInStr, "desiredMoveInDate", "desired_move_in_date") &&
                DateTime.TryParse(moveInStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var moveIn))
            {
                fields.DesiredMoveInDate = moveIn;
            }
            if (TryGetOverrideString(root, out var idLast4, "idLast4", "id_last4"))
                fields.IdLast4 = idLast4;
            if (TryGetOverrideString(root, out var coSigner, "coSignerName", "co_signer_name"))
                fields.CoSignerName = coSigner;
            if (TryGetOverrideString(root, out var notes, "notes"))
                fields.Notes = notes;
            // Reviewer can correct the property/unit link; 0 clears it back to "no property".
            if (TryGetOverrideInt(root, out var propertyId, "propertyId", "property_id"))
                fields.PropertyId = propertyId > 0 ? propertyId : null;
            if (TryGetOverrideInt(root, out var unitId, "unitId", "unit_id"))
                fields.UnitId = unitId > 0 ? unitId : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse application overridesJson; skipping overrides.");
        }
    }

    /// <summary>Parses an ISO date from a field's "value" sub-property, normalized to UTC. Null on failure.</summary>
    private static DateTime? ParseDateField(JsonElement root, string key)
    {
        var str = ReadFieldValue(root, key);
        if (string.IsNullOrWhiteSpace(str)) return null;
        return DateTime.TryParse(str, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
    }

    private static int? ParseIntField(JsonElement root, string key)
    {
        var str = ReadFieldValue(root, key);
        return int.TryParse(str, out var value) ? value : null;
    }

    /// <summary>First present key wins. Accepts JSON string or number (number returned as text).</summary>
    private static bool TryGetOverrideString(JsonElement root, out string? value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var el))
            {
                if (el.ValueKind == JsonValueKind.String) { value = el.GetString(); return true; }
                if (el.ValueKind == JsonValueKind.Number) { value = el.GetRawText(); return true; }
            }
        }
        value = null;
        return false;
    }

    /// <summary>First present key wins. Accepts a JSON number or a numeric string.</summary>
    private static bool TryGetOverrideDecimal(JsonElement root, out decimal value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out value)) return true;
                if (el.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(el.GetString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
            }
        }
        value = 0m;
        return false;
    }

    /// <summary>First present key wins. Accepts a JSON integer number or a numeric string.</summary>
    private static bool TryGetOverrideInt(JsonElement root, out int value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;
                if (el.ValueKind == JsonValueKind.String &&
                    int.TryParse(el.GetString(), out value)) return true;
            }
        }
        value = 0;
        return false;
    }

    private sealed class WorkOrderDraftFields
    {
        public int PropertyId { get; set; }
        public int? UnitId { get; set; }
        public int? TenantId { get; set; }
        public int? LeaseId { get; set; }
        public int? VendorId { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;
        public decimal? EstimatedCost { get; set; }
    }

    private sealed class LeaseDraftFields
    {
        public int PropertyId { get; set; }
        public int? UnitId { get; set; }
        public int? TenantId { get; set; }
        public string? TenantName { get; set; }

        // Leased-premises text extracted straight off the document, used to match-or-create the
        // Property/Unit when no in-portfolio id was matched (the empty-portfolio bootstrap path).
        public string? PropertyName { get; set; }
        public string? PropertyType { get; set; }
        public string? PropertyAddress { get; set; }
        public string? PropertyCity { get; set; }
        public string? PropertyState { get; set; }
        public string? PropertyPostalCode { get; set; }
        public string? UnitNumber { get; set; }
        public decimal? UnitBedrooms { get; set; }
        public decimal? UnitBathrooms { get; set; }
        public int? UnitSquareFeet { get; set; }

        public string? LeaseNumber { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public decimal? MonthlyRent { get; set; }
        public decimal? SecurityDeposit { get; set; }
        public decimal? LateFee { get; set; }
        public int? RentDueDay { get; set; }
    }

    private sealed class ApplicationDraftFields
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? CurrentAddress { get; set; }
        public string? Employer { get; set; }
        public decimal? MonthlyIncome { get; set; }
        public DateTime? DesiredMoveInDate { get; set; }

        // Application-only details with no dedicated RentalApplication column — folded into Notes on confirm.
        public string? ApplyingFor { get; set; }
        public string? IdLast4 { get; set; }
        public string? CoSignerName { get; set; }
        public string? Notes { get; set; }

        // Optional in-portfolio property/unit the applicant is applying for (validated before trust).
        public int? PropertyId { get; set; }
        public int? UnitId { get; set; }
    }
}
