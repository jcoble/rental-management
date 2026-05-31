using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Imaging;
using RentalCommand.Api.Services.Domain;
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
    private readonly IAuditTrailService _audit;
    private readonly ILogger<ScanService> _logger;

    public ScanService(
        RentalCommandDbContext db,
        IScanFileService files,
        IExpenseService expenses,
        IPaymentService payments,
        IAuditTrailService audit,
        ILogger<ScanService> logger)
    {
        _db = db;
        _files = files;
        _expenses = expenses;
        _payments = payments;
        _audit = audit;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // CreateDraftAsync
    // -------------------------------------------------------------------------

    public async Task<ScanDraft> CreateDraftAsync(
        int portfolioId,
        byte[] fileBytes,
        string contentType,
        string targetEntityType,
        CancellationToken ct = default)
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

        if (draft.TargetEntityType is not ("Expense" or "Payment"))
            return new ScanConfirmResult(false, null, $"Unsupported target '{draft.TargetEntityType}'");

        // Atomically claim the draft so two concurrent confirms can't both create an entity.
        // The conditional UPDATE only matches a not-yet-finalized, not-in-flight draft; the DB
        // serializes concurrent callers so exactly one wins (affected == 1).
        var claimed = await _db.ScanDrafts
            .Where(d => d.Id == draftId && d.PortfolioId == portfolioId
                && d.Status != "Confirmed" && d.Status != "Rejected" && d.Status != "Confirming")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "Confirming"), ct);

        if (claimed == 0)
            return new ScanConfirmResult(false, null, "Draft is already being confirmed or finalized");

        // Start from the extracted fields, then apply the user's reviewed overrides (overrides win).
        var dto = BuildReceiptDto(draft.ExtractedFields);
        ApplyOverrides(dto, overridesJson);

        // ---- ROUTER ----
        if (draft.TargetEntityType == "Payment")
            return await ConfirmAsPaymentAsync(portfolioId, draftId, userId, draft, dto, overridesJson, ct);

        // Default: Expense path (unchanged).
        return await ConfirmAsExpenseAsync(portfolioId, draftId, userId, draft, dto, overridesJson, ct);
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
        // Determine paid vs unpaid.
        // The review UI may send is_paid explicitly; if absent, derive from document_kind.
        bool isPaid;
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

        var request = new CreateExpenseRequest
        {
            Category    = dto.Category ?? ScheduleECategory.Other,
            Description = string.IsNullOrWhiteSpace(dto.VendorName) ? "Scanned receipt" : dto.VendorName!,
            Amount      = dto.Total ?? dto.Subtotal ?? 0m,
            Subtotal    = dto.Subtotal,
            TaxAmount   = dto.Tax,
            IncurredAt  = dto.TransactionDate ?? DateTime.UtcNow,
            BillableToOwner = false,
            Notes       = dto.Notes,
            ReceiptData = receiptDataJson,
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Expense creation threw while confirming scan draft {DraftId}", draftId);
            expense = null;
        }

        if (expense is null)
        {
            await ReleaseClaim(portfolioId, draftId, ct);
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

        return new ScanConfirmResult(true, expense.Id, null);
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
            await ReleaseClaim(portfolioId, draftId, ct);
            return new ScanConfirmResult(false, null, "Select a lease for this payment");
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
            Amount            = dto.Total ?? dto.Subtotal ?? 0m,
            DueDate           = paymentDate,
            PaidDate          = paymentDate,
            Method            = "Check",
            ExternalReference = dto.CheckNumber,
            Notes             = string.IsNullOrWhiteSpace(notes) ? null : notes,
        };

        PaymentResponse? payment;
        try
        {
            payment = await _payments.CreateAsync(portfolioId, paymentRequest, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payment creation threw while confirming scan draft {DraftId}", draftId);
            payment = null;
        }

        if (payment is null)
        {
            await ReleaseClaim(portfolioId, draftId, ct);
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

        return new ScanConfirmResult(true, payment.Id, null);
    }

    // -------------------------------------------------------------------------
    // Shared finalize helpers
    // -------------------------------------------------------------------------

    /// <summary>Releases the "Confirming" claim back to "Reviewing" so the user can retry.</summary>
    private Task ReleaseClaim(int portfolioId, int draftId, CancellationToken ct) =>
        _db.ScanDrafts
            .Where(d => d.Id == draftId && d.PortfolioId == portfolioId && d.Status == "Confirming")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "Reviewing"), ct);

    /// <summary>
    /// Re-keys the StoredFile to the newly created entity, then marks the draft Confirmed.
    /// Called by both Expense and Payment branches after successful entity creation.
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

        if (draft.Status is "Confirmed" or "Rejected")
            return false; // already finalized — don't reject a confirmed (already-created) record

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
            dto.Total    = ParseDecimalField(root, "total");

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
    /// Never throws on null/empty/malformed JSON. Line items are not editable via overrides.
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

            if (TryGetOverrideDecimal(root, out var total, "total"))
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse overridesJson; skipping overrides.");
        }
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
}
