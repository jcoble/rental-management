using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
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
    private readonly IAuditTrailService _audit;
    private readonly ILogger<ScanService> _logger;

    public ScanService(
        RentalCommandDbContext db,
        IScanFileService files,
        IExpenseService expenses,
        IAuditTrailService audit,
        ILogger<ScanService> logger)
    {
        _db = db;
        _files = files;
        _expenses = expenses;
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

        var draft = new ScanDraft
        {
            PortfolioId = portfolioId,
            FilePath = stored.FilePath,
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

        if (draft.TargetEntityType != "Expense")
            return new ScanConfirmResult(false, null, $"Unsupported target '{draft.TargetEntityType}'");

        // Atomically claim the draft so two concurrent confirms can't both create an Expense.
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

        var request = new CreateExpenseRequest
        {
            Category = dto.Category ?? ScheduleECategory.Other,
            Description = string.IsNullOrWhiteSpace(dto.VendorName) ? "Scanned receipt" : dto.VendorName!,
            Status = ExpenseStatus.Pending,
            Amount = dto.Amount ?? 0m,
            IncurredAt = dto.TransactionDate ?? DateTime.UtcNow,
            BillableToOwner = false,
            Notes = dto.Notes,
        };

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
            // Release the claim so the user can retry from the review page.
            await _db.ScanDrafts
                .Where(d => d.Id == draftId && d.PortfolioId == portfolioId && d.Status == "Confirming")
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "Reviewing"), ct);
            return new ScanConfirmResult(false, null, "Expense creation failed");
        }

        // Re-key the uploaded file to the new Expense and finalize the draft.
        await _db.StoredFiles
            .Where(f => f.PortfolioId == portfolioId && f.FilePath == draft.FilePath)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.EntityType, "Expense")
                .SetProperty(f => f.EntityId, (int?)expense.Id), ct);

        await _db.ScanDrafts
            .Where(d => d.Id == draftId && d.PortfolioId == portfolioId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, "Confirmed")
                .SetProperty(d => d.ConfirmedAt, (DateTime?)DateTime.UtcNow)
                .SetProperty(d => d.ReviewedBy, userId.ToString()), ct);

        // Audit the values actually applied (post-override), with the raw extraction as the prior state.
        var appliedJson = JsonSerializer.Serialize(new
        {
            vendorName = dto.VendorName,
            amount = dto.Amount,
            transactionDate = dto.TransactionDate,
            category = dto.Category?.ToString(),
            notes = dto.Notes,
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
    /// <c>{"vendor_name":{"value":"...","confidence":0.9}, "amount":{...}, ...}</c>
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

            dto.VendorName = ReadFieldValue(root, "vendor_name");

            var amountStr = ReadFieldValue(root, "amount");
            if (!string.IsNullOrWhiteSpace(amountStr) &&
                decimal.TryParse(amountStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var amount))
            {
                dto.Amount = amount;
            }

            var dateStr = ReadFieldValue(root, "transaction_date");
            if (!string.IsNullOrWhiteSpace(dateStr) &&
                DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            {
                dto.TransactionDate = date;
            }

            var categoryStr = ReadFieldValue(root, "category");
            if (!string.IsNullOrWhiteSpace(categoryStr) &&
                Enum.TryParse<ScheduleECategory>(categoryStr, ignoreCase: true, out var category))
            {
                dto.Category = category;
            }

            dto.Notes = ReadFieldValue(root, "notes");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse extractedFieldsJson; returning empty ReceiptDto.");
        }

        return dto;
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
    /// Merges a flat override JSON <c>{"vendorName":"...","amount":12.34,"transactionDate":"...","category":"...","notes":"..."}</c>
    /// into the DTO, overwriting any present key. Never throws on null/empty/malformed JSON.
    /// </summary>
    private void ApplyOverrides(ExtractedReceiptDto dto, string overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(overridesJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("vendorName", out var vn) && vn.ValueKind == JsonValueKind.String)
                dto.VendorName = vn.GetString();

            if (root.TryGetProperty("amount", out var amt))
            {
                if (amt.ValueKind == JsonValueKind.Number && amt.TryGetDecimal(out var d))
                    dto.Amount = d;
                else if (amt.ValueKind == JsonValueKind.String &&
                         decimal.TryParse(amt.GetString(), System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var ds))
                    dto.Amount = ds;
            }

            if (root.TryGetProperty("transactionDate", out var td) && td.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(td.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                dto.TransactionDate = parsedDate;
            }

            if (root.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String &&
                Enum.TryParse<ScheduleECategory>(cat.GetString(), ignoreCase: true, out var parsedCat))
            {
                dto.Category = parsedCat;
            }

            if (root.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String)
                dto.Notes = notes.GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse overridesJson; skipping overrides.");
        }
    }
}
