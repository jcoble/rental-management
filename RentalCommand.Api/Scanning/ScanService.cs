using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Scanning;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Implements the post-upload scan lifecycle: prepares typed atomic confirmation commands and
/// handles review/reject transitions. Blob admission and draft creation belong to
/// <see cref="IScanUploadService"/>.
/// </summary>
public sealed class ScanService : IScanService
{
    private readonly RentalCommandDbContext _db;
    private readonly IAuditTrailService _audit;
    private readonly ILogger<ScanService> _logger;
    private readonly TimeProvider _timeProvider;
    private static readonly Regex ExpenseUnitReferenceRegex = new(
        @"\b(?:unit|apt|apartment)\s*(?:#|:)?\s*([A-Za-z0-9][A-Za-z0-9-]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex ApplicationUnitReferenceRegex = new(
        @"\b(?:unit|apt|apartment)\s*(?:#|:)?\s*(?<unit>[A-Za-z0-9][A-Za-z0-9-]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public ScanService(
        RentalCommandDbContext db,
        IAuditTrailService audit,
        ILogger<ScanService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    // -------------------------------------------------------------------------
    // PrepareConfirmationAsync
    // -------------------------------------------------------------------------

    public async Task<ScanConfirmationPreparation> PrepareConfirmationAsync(
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
            return new(ScanConfirmationPreparationOutcome.DraftNotFound, Error: "Scan draft not found.");

        if (!Enum.TryParse<ScanConfirmationTargetKind>(
                draft.TargetEntityType, ignoreCase: true, out var kind))
        {
            return new(
                ScanConfirmationPreparationOutcome.UnsupportedTarget,
                Error: $"Unsupported scan confirmation target '{draft.TargetEntityType}'.");
        }
        var normalizedOverrides = string.IsNullOrWhiteSpace(overridesJson) ? "{}" : overridesJson;
        JsonElement overrideRoot;
        try
        {
            using var overrideDocument = JsonDocument.Parse(normalizedOverrides);
            if (overrideDocument.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Scan confirmation overrides must be a JSON object.");
            overrideRoot = overrideDocument.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ScanConfirmationValidationException($"Invalid scan confirmation overrides: {ex.Message}");
        }

        ScanConfirmationTargetData target;
        if (kind is ScanConfirmationTargetKind.Expense or ScanConfirmationTargetKind.Payment)
        {
            var receipt = BuildReceiptDto(draft.ExtractedFields);
            ApplyOverrides(receipt, normalizedOverrides);
            var receiptData = ToAtomicReceipt(receipt);
            if (kind == ScanConfirmationTargetKind.Payment)
            {
                var tenantAccountId = PositiveOverride(
                        overrideRoot, "tenantAccountId", "tenant_account_id")
                    ?? draft.CaptureTenantAccountId
                    ?? 0;
                var tenantLedgerEntryId = PositiveLongOverride(
                        overrideRoot, "tenantLedgerEntryId", "tenant_ledger_entry_id")
                    ?? draft.CaptureTenantLedgerEntryId;
                if (tenantAccountId <= 0)
                {
                    throw new ScanConfirmationValidationException(
                        "Select the rental account this payment belongs to.");
                }
                target = new(kind, Payment: new ScanPaymentTargetData(
                    receiptData, tenantAccountId, tenantLedgerEntryId));
            }
            else
            {
                var isPaid = TryGetOverrideBool(overrideRoot, out var paid, "is_paid", "isPaid")
                    ? paid
                    : receipt.DocumentKind is not ("Bill" or "Invoice" or "UtilityBill" or "PropertyTax");
                var propertyId = PositiveOverride(overrideRoot, "propertyId", "property_id")
                    ?? draft.CapturePropertyId;
                var unitId = PositiveOverride(overrideRoot, "unitId", "unit_id")
                    ?? draft.CaptureUnitId;
                var workOrderId = PositiveOverride(overrideRoot, "workOrderId", "work_order_id")
                    ?? draft.CaptureWorkOrderId;
                if (unitId is null && propertyId is int selectedPropertyId)
                    unitId = await TryResolveExpenseUnitFromNotesAsync(
                        portfolioId, selectedPropertyId, receipt.Notes, ct);
                target = new(
                    kind,
                    Expense: new ScanExpenseTargetData(receiptData, isPaid, propertyId, unitId, workOrderId));
            }
        }
        else if (kind == ScanConfirmationTargetKind.WorkOrder)
        {
            var fields = BuildWorkOrderFields(draft.ExtractedFields);
            await ValidateWorkOrderIdsInPortfolioAsync(portfolioId, fields, ct);
            ApplyWorkOrderOverrides(fields, normalizedOverrides);
            if (fields.PropertyId <= 0)
                fields.PropertyId = draft.CapturePropertyId ?? 0;
            fields.UnitId ??= draft.CaptureUnitId;
            fields.LeaseManagementId ??= draft.CaptureLeaseManagementId;
            if (fields.UnitId is null && fields.PropertyId > 0)
                fields.UnitId = await TryResolveWorkOrderUnitFromTextAsync(
                    portfolioId, fields.PropertyId, fields, ct);
            target = new(kind, WorkOrder: new ScanWorkOrderTargetData(
                fields.PropertyId, fields.UnitId, fields.TenantId, fields.LeaseManagementId, fields.VendorId,
                fields.Title, fields.Description, fields.Category, fields.Priority, fields.EstimatedCost));
        }
        else if (kind == ScanConfirmationTargetKind.LeaseAgreement)
        {
            var fields = BuildLeaseFields(draft.ExtractedFields);
            await ValidateLeaseIdsInPortfolioAsync(portfolioId, fields, ct);
            ApplyLeaseOverrides(fields, normalizedOverrides);

            fields.PropertyId = PositiveOverride(overrideRoot, "propertyId", "property_id")
                ?? (fields.PropertyId > 0 ? fields.PropertyId : draft.CapturePropertyId ?? 0);
            fields.UnitId = PositiveOverride(overrideRoot, "unitId", "unit_id")
                ?? fields.UnitId ?? draft.CaptureUnitId;
            var leaseManagementId = PositiveOverride(
                overrideRoot, "leaseManagementId", "lease_management_id")
                ?? draft.CaptureLeaseManagementId;
            var tenantAccountId = PositiveOverride(
                overrideRoot, "tenantAccountId", "tenant_account_id")
                ?? draft.CaptureTenantAccountId;
            var leaseAgreementId = PositiveOverride(
                overrideRoot, "leaseAgreementId", "lease_agreement_id")
                ?? draft.CaptureLeaseAgreementId;
            var templateId = PositiveOverride(
                overrideRoot, "documentTemplateId", "document_template_id");
            var dispositionText = TryGetOverrideString(
                overrideRoot, out var suppliedDisposition,
                "reviewDisposition", "review_disposition")
                ? suppliedDisposition
                : null;
            if (!Enum.TryParse<LeaseScanReviewDisposition>(
                    dispositionText, ignoreCase: true, out var disposition))
            {
                throw new ScanConfirmationValidationException(
                    "Choose whether the uploaded lease is AlreadyFullySigned or NeedsSignatures.");
            }

            target = new(kind, LeaseAgreement: new ScanLeaseTargetData(
                fields.PropertyId, fields.UnitId, fields.TenantId, fields.TenantName,
                fields.TenantEmail, fields.TenantPhone, fields.TenantEmergencyContact,
                fields.PropertyName, fields.PropertyType, fields.PropertyAddress, fields.PropertyCity,
                fields.PropertyState, fields.PropertyPostalCode, fields.UnitNumber,
                fields.UnitBedrooms, fields.UnitBathrooms, fields.UnitSquareFeet, fields.LeaseNumber,
                fields.StartDate, fields.EndDate, fields.MonthlyRent, fields.SecurityDeposit,
                fields.LateFee, fields.RentDueDay,
                disposition, leaseManagementId, tenantAccountId, leaseAgreementId, templateId,
                TermsSchemaVersion: 1,
                TermsPayload: string.IsNullOrWhiteSpace(draft.ExtractedFields) ? "{}" : draft.ExtractedFields,
                GracePeriodDays: 0));
        }
        else if (kind == ScanConfirmationTargetKind.Application)
        {
            var fields = BuildApplicationFields(draft.ExtractedFields);
            await ValidateApplicationIdsInPortfolioAsync(portfolioId, fields, ct);
            ApplyApplicationOverrides(fields, normalizedOverrides);
            fields.PropertyId ??= draft.CapturePropertyId;
            fields.UnitId ??= draft.CaptureUnitId;
            int? propertyId = fields.PropertyId is > 0
                && await _db.EnsurePropertyInPortfolioAsync(portfolioId, fields.PropertyId.Value, ct)
                ? fields.PropertyId
                : null;
            int? unitId = fields.UnitId is > 0
                && await _db.EnsureUnitInPortfolioAsync(portfolioId, fields.UnitId.Value, propertyId, ct)
                ? fields.UnitId
                : null;
            if (propertyId is null || unitId is null)
            {
                var requestedHome = await TryResolveApplicationRequestedHomeAsync(
                    portfolioId, fields.ApplyingFor, propertyId, ct);
                propertyId ??= requestedHome?.PropertyId;
                unitId ??= requestedHome?.UnitId;
            }
            target = new(kind, Application: new ScanApplicationTargetData(
                fields.FirstName, fields.LastName, fields.Email, fields.Phone, fields.DateOfBirth,
                fields.CurrentAddress, fields.Employer, fields.MonthlyIncome, fields.DesiredMoveInDate,
                fields.ApplyingFor, fields.IdLast4, fields.CoSignerName, fields.Notes, propertyId, unitId));
        }
        else
        {
            var fields = BuildLoanFields(draft.ExtractedFields);
            await ValidateLoanIdsInPortfolioAsync(portfolioId, fields, ct);
            ApplyLoanOverrides(fields, normalizedOverrides);
            target = new(kind, Loan: new ScanLoanTargetData(
                fields.PropertyId, fields.Lender, fields.OriginalAmount, fields.CurrentBalance,
                fields.AnnualInterestRatePct, fields.TermMonths, fields.StartDate, fields.DayOfMonthDue,
                fields.MonthlyPrincipalInterest, fields.MonthlyEscrow, fields.EscrowCoversTaxes,
                fields.EscrowCoversInsurance, fields.Notes));
        }

        return new(
            ScanConfirmationPreparationOutcome.Ready,
            new ConfirmScanDraftCommand(
                portfolioId,
                draftId,
                userId,
                _timeProvider.UtcNow(),
                ScanConfirmationDraftFingerprint.Create(
                    draft.TargetEntityType,
                    draft.SourceStoredFileId,
                    draft.ExtractedFields,
                    draft.SourceContentSha256,
                    draft.CaptureAccessContextId,
                    draft.CaptureAccessRevision,
                    draft.CapturePropertyId,
                    draft.CaptureUnitId,
                    draft.CaptureLeaseManagementId,
                    draft.CaptureLeaseAgreementId,
                    draft.CaptureTenantAccountId,
                    draft.CaptureTenantLedgerEntryId,
                    draft.CaptureWorkOrderId,
                    draft.CaptureApplicationId,
                    draft.CaptureRentalListingId,
                    draft.SourceLabel),
                target,
                draft.SourceStoredFileId,
                SourceContentSha256: draft.SourceContentSha256,
                SourceLabel: draft.SourceLabel,
                CaptureContext: new ScanCaptureContextData(
                    draft.CaptureExperience,
                    draft.CaptureAccessContextId,
                    draft.CaptureAccessRevision,
                    draft.CapturePropertyId,
                    draft.CaptureUnitId,
                    draft.CaptureLeaseManagementId,
                    draft.CaptureLeaseAgreementId,
                    draft.CaptureTenantAccountId,
                    draft.CaptureTenantLedgerEntryId,
                    draft.CaptureWorkOrderId,
                    draft.CaptureApplicationId,
                    draft.CaptureRentalListingId,
                    draft.SourceLabel)));
    }

    private static int? PositiveOverride(JsonElement root, params string[] names) =>
        TryGetOverrideInt(root, out var value, names) && value > 0 ? value : null;

    private static long? PositiveLongOverride(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric) && numeric > 0)
                return numeric;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), out numeric)
                && numeric > 0)
                return numeric;
        }
        return null;
    }

    private static ScanReceiptData ToAtomicReceipt(ExtractedReceiptDto receipt) => new(
        receipt.VendorName, receipt.VendorAddress, receipt.VendorPhone, receipt.VendorWebsite,
        receipt.VendorTaxId, receipt.ReceiptNumber, receipt.TransactionDate, receipt.Subtotal,
        receipt.Tax, receipt.TaxRate, receipt.Tip, receipt.Discount, receipt.Shipping, receipt.Total,
        receipt.PaymentMethod, receipt.CardLast4, receipt.Category, receipt.DocumentKind, receipt.Notes,
        receipt.DueDate,
        receipt.LineItems.Select(line => new ScanReceiptLineData(
            line.Description, line.Quantity, line.UnitPrice, line.Amount)).ToArray(),
        receipt.PayerName, receipt.CheckNumber, receipt.BankName,
        receipt.Extra.Select(pair => new ScanExtraFieldData(pair.Key, pair.Value)).ToArray());

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
        if (draft is null || draft.TargetEntityType is not nameof(LeaseAgreement))
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
                // Canonical lease import never silently creates the physical inventory. Preserve
                // the extracted suggestion while requiring the reviewer to select (or first add)
                // the actual Property that will own the rental relationship.
                var label = !string.IsNullOrWhiteSpace(fields.PropertyName)
                    ? fields.PropertyName!.Trim()
                    : fields.PropertyAddress!.Trim();
                propertyProposal = new ProposedRecord("select", null, label,
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
                : new ProposedRecord("select", null, $"Unit {unitNumber}", null);
        }
        else
        {
            // The extracted Unit label remains a useful suggestion, but its canonical inventory row
            // must be explicitly selected before confirmation.
            unitProposal = new ProposedRecord("select", null, $"Unit {unitNumber}", null);
        }

        return new LeaseImportProposal(propertyProposal, unitProposal);
    }

    /// <summary>
    /// Best-effort requested-home matcher for scanned paper applications. It only links existing rows:
    /// "Summit Row Unit 4D" resolves by exact property-name + unit-number match, and "Unit 4D" resolves
    /// only when that unit number is unique in the portfolio. Ambiguous/free-text cases remain unlinked.
    /// </summary>
    private async Task<ApplicationHomeMatch?> TryResolveApplicationRequestedHomeAsync(
        int portfolioId,
        string? applyingFor,
        int? knownPropertyId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(applyingFor))
            return null;

        var unitMatch = ApplicationUnitReferenceRegex.Match(applyingFor);
        if (!unitMatch.Success)
            return null;

        var unitKey = CollapseWhitespace(unitMatch.Groups["unit"].Value).ToLowerInvariant();
        if (unitKey.Length == 0)
            return null;

        if (knownPropertyId is > 0)
        {
            return await _db.Units
                .AsNoTracking()
                .Where(u => u.PropertyId == knownPropertyId.Value && u.DeletedAt == null)
                .Where(u => u.Property != null && u.Property.PortfolioId == portfolioId && u.Property.DeletedAt == null)
                .Where(u => (u.UnitNumber ?? "").Trim().ToLower() == unitKey)
                .OrderBy(u => u.Id)
                .Select(u => new ApplicationHomeMatch(u.PropertyId, u.Id))
                .FirstOrDefaultAsync(ct);
        }

        var propertyHint = ExtractApplicationPropertyHint(applyingFor, unitMatch);
        if (!string.IsNullOrWhiteSpace(propertyHint))
        {
            var propertyKey = CollapseWhitespace(propertyHint).ToLowerInvariant();
            return await _db.Units
                .AsNoTracking()
                .Where(u => u.DeletedAt == null)
                .Where(u => u.Property != null && u.Property.PortfolioId == portfolioId && u.Property.DeletedAt == null)
                .Where(u => (u.UnitNumber ?? "").Trim().ToLower() == unitKey)
                .Where(u => (u.Property!.Name ?? "").Trim().ToLower() == propertyKey)
                .OrderBy(u => u.Id)
                .Select(u => new ApplicationHomeMatch(u.PropertyId, u.Id))
                .FirstOrDefaultAsync(ct);
        }

        var unitOnlyMatch = await _db.Units
            .AsNoTracking()
            .Where(u => u.DeletedAt == null)
            .Where(u => u.Property != null && u.Property.PortfolioId == portfolioId && u.Property.DeletedAt == null)
            .Where(u => (u.UnitNumber ?? "").Trim().ToLower() == unitKey)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                PropertyId = group.Min(unit => unit.PropertyId),
                UnitId = group.Min(unit => unit.Id),
            })
            .SingleOrDefaultAsync(ct);

        return unitOnlyMatch?.Count == 1
            ? new ApplicationHomeMatch(unitOnlyMatch.PropertyId, unitOnlyMatch.UnitId)
            : null;
    }

    private static string? ExtractApplicationPropertyHint(string applyingFor, Match unitMatch)
    {
        var beforeUnit = applyingFor[..unitMatch.Index];
        var afterUnit = applyingFor[(unitMatch.Index + unitMatch.Length)..];

        var hint = CleanApplicationPropertyHint(beforeUnit);
        if (!string.IsNullOrWhiteSpace(hint))
            return hint;

        hint = CleanApplicationPropertyHint(afterUnit);
        return string.IsNullOrWhiteSpace(hint) ? null : hint;
    }

    private static string? CleanApplicationPropertyHint(string value)
    {
        var hint = value.Trim(' ', '\t', '\r', '\n', ',', '-', ':', ';', '.', '#');
        foreach (var prefix in new[] { "at ", "for " })
        {
            if (hint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                hint = hint[prefix.Length..].Trim(' ', '\t', '\r', '\n', ',', '-', ':', ';', '.', '#');
                break;
            }
        }

        return string.IsNullOrWhiteSpace(hint) ? null : hint;
    }

    /// <summary>A property the lease's extracted address/name matched in this portfolio, or null.</summary>
    private sealed record PropertyMatch(int Id, string Label, string? Detail);

    /// <summary>A unit the lease's extracted unit number matched under the property, or null.</summary>
    private sealed record UnitMatch(int Id, string Label);

    /// <summary>An existing property/unit pair matched from a scanned application's requested-home text.</summary>
    private sealed record ApplicationHomeMatch(int PropertyId, int UnitId);

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

        if (!string.IsNullOrWhiteSpace(addressKey))
        {
            var aliases = AddressAliases(addressKey);
            var byAddress = await _db.Properties
                .AsNoTracking()
                .Where(property => property.PortfolioId == portfolioId && property.DeletedAt == null)
                .Where(property => aliases.Contains(
                    (property.AddressLine1 ?? string.Empty).Trim().ToLower()
                        .Replace(".", string.Empty).Replace(",", string.Empty)
                        .Replace("  ", " ").Replace("  ", " ")))
                .Where(property => cityKey.Length == 0
                    || property.City == null
                    || property.City.Trim() == string.Empty
                    || property.City.Trim().ToLower() == cityKey)
                .OrderBy(property => property.Id)
                .Select(property => new { property.Id, property.Name, property.AddressLine1, property.City })
                .FirstOrDefaultAsync(ct);
            if (byAddress is not null)
                return new PropertyMatch(byAddress.Id, byAddress.Name, FormatAddress(byAddress.AddressLine1, byAddress.City));
        }
        else
        {
            var byName = await _db.Properties
                .AsNoTracking()
                .Where(property => property.PortfolioId == portfolioId && property.DeletedAt == null)
                .Where(property => (property.Name ?? string.Empty).Trim().ToLower()
                    .Replace("  ", " ").Replace("  ", " ") == nameKey)
                .OrderBy(property => property.Id)
                .Select(property => new { property.Id, property.Name, property.AddressLine1, property.City })
                .FirstOrDefaultAsync(ct);
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

        return await _db.Units
            .Where(u => u.PropertyId == propertyId && u.DeletedAt == null)
            .Where(u => (u.UnitNumber ?? "").Trim().ToLower() == unitKey)
            .OrderBy(u => u.Id)
            .Select(u => new UnitMatch(u.Id, u.UnitNumber))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<int?> TryResolveExpenseUnitFromNotesAsync(
        int portfolioId,
        int propertyId,
        string? notes,
        CancellationToken ct)
        => await TryResolveUnitFromTextAsync(portfolioId, propertyId, notes, ct);

    private async Task<int?> TryResolveWorkOrderUnitFromTextAsync(
        int portfolioId,
        int propertyId,
        WorkOrderDraftFields fields,
        CancellationToken ct)
    {
        var text = string.Join(
            " ",
            new[] { fields.Title, fields.Description, fields.Category }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return await TryResolveUnitFromTextAsync(portfolioId, propertyId, text, ct);
    }

    private async Task<int?> TryResolveUnitFromTextAsync(
        int portfolioId,
        int propertyId,
        string? text,
        CancellationToken ct)
    {
        var unitNumber = ExtractUnitNumberFromText(text);
        if (string.IsNullOrWhiteSpace(unitNumber))
            return null;

        var unitKey = CollapseWhitespace(unitNumber).ToLowerInvariant();

        return await _db.Units
            .Where(u => u.PropertyId == propertyId && u.DeletedAt == null)
            .Where(u => u.Property != null && u.Property.PortfolioId == portfolioId && u.Property.DeletedAt == null)
            .Where(u => (u.UnitNumber ?? "").Trim().ToLower() == unitKey)
            .OrderBy(u => u.Id)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static string? ExtractUnitNumberFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = ExpenseUnitReferenceRegex.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
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
    /// existing-inventory match forgiving of the formatting noise typical of scanned leases.
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

    private static string[] AddressAliases(string normalizedAddress)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal) { normalizedAddress };
        var tokens = normalizedAddress.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length; index++)
        {
            foreach (var alternative in StreetSuffixAbbreviations
                         .Where(pair => pair.Value == tokens[index])
                         .Select(pair => pair.Key)
                         .Distinct(StringComparer.Ordinal))
            {
                var variant = (string[])tokens.Clone();
                variant[index] = alternative;
                aliases.Add(string.Join(' ', variant));
            }
        }

        return aliases.ToArray();
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

    // -------------------------------------------------------------------------
    // RejectDraftAsync
    // -------------------------------------------------------------------------

    public async Task<bool> RejectDraftAsync(
        WorkspaceReadScope scope,
        int draftId,
        int userId,
        string? reason,
        CancellationToken ct = default)
    {
        _audit.EnsureAtomicCommand();
        return await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            // The session, access revision, target capability, and property scope are re-read in
            // this transaction. A controller check is deliberately insufficient for a mutation.
            var draft = await _db.ScanDrafts
                .WhereAuthorizedForReview(
                    _db, scope, _timeProvider.GetUtcNow().UtcDateTime)
                .AsTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == draftId, innerCt);

            if (draft is null || draft.Status is "Confirmed" or "Rejected" or "Confirming")
                return false;

            draft.Status = "Rejected";
            draft.ReviewedAt = _timeProvider.UtcNow();
            draft.ReviewedBy = userId.ToString();
            var rejectionReason = Truncate(reason?.Trim(), 500);
            if (rejectionReason is not null)
                draft.FailureReason = rejectionReason;
            await _db.SaveChangesAsync(innerCt);

            await _audit.LogAsync(
                scope.PortfolioId,
                "ScanDraft",
                draftId,
                AuditLogOperation.Rejected,
                userId: userId,
                changeReason: reason,
                ct: innerCt);

            return true;
        }, ct);
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];

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
            dto.Category = ParseScheduleECategory(ReadFieldValue(root, "category"));

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

    private static ScheduleECategory? ParseScheduleECategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Enum.TryParse<ScheduleECategory>(value, ignoreCase: true, out var category))
            return category;

        return ScheduleECategoryMap.FromAccountName(value);
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

            if (TryGetOverrideString(root, out var catStr, "category"))
                dto.Category = ParseScheduleECategory(catStr);

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
            fields.LeaseManagementId = ParseIntField(root, "lease_management_id") ?? ParseIntField(root, "leaseManagementId");
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

        if (fields.LeaseManagementId is > 0 &&
            !await _db.EnsureLeaseManagementInPortfolioAsync(portfolioId, fields.LeaseManagementId.Value, ct))
        {
            fields.LeaseManagementId = null;
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

            if (TryGetOverrideNullableInt(root, out var propertyId, "propertyId", "property_id"))
                fields.PropertyId = propertyId.GetValueOrDefault();
            if (TryGetOverrideNullableInt(root, out var unitId, "unitId", "unit_id"))
                fields.UnitId = unitId is > 0 ? unitId : null;
            if (TryGetOverrideNullableInt(root, out var tenantId, "tenantId", "tenant_id"))
                fields.TenantId = tenantId is > 0 ? tenantId : null;
            if (TryGetOverrideNullableInt(root, out var leaseManagementId, "leaseManagementId", "lease_management_id"))
                fields.LeaseManagementId = leaseManagementId is > 0 ? leaseManagementId : null;
            if (TryGetOverrideNullableInt(root, out var vendorId, "vendorId", "vendor_id"))
                fields.VendorId = vendorId is > 0 ? vendorId : null;
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
            fields.TenantEmail = ReadFieldValue(root, "tenant_email") ?? ReadFieldValue(root, "tenantEmail");
            fields.TenantPhone = ReadFieldValue(root, "tenant_phone") ?? ReadFieldValue(root, "tenantPhone");
            fields.TenantEmergencyContact = ReadFieldValue(root, "tenant_emergency_contact")
                ?? ReadFieldValue(root, "tenantEmergencyContact")
                ?? ReadFieldValue(root, "emergency_contact")
                ?? ReadFieldValue(root, "emergencyContact");
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

            if (TryGetOverrideNullableInt(root, out var propertyId, "propertyId", "property_id"))
                fields.PropertyId = propertyId.GetValueOrDefault();
            if (TryGetOverrideNullableInt(root, out var unitId, "unitId", "unit_id"))
                fields.UnitId = unitId is > 0 ? unitId : null;
            if (TryGetOverrideNullableInt(root, out var tenantId, "tenantId", "tenant_id"))
                fields.TenantId = tenantId is > 0 ? tenantId : null;
            if (TryGetOverrideString(root, out var tenantName, "tenantName", "tenant_name"))
                fields.TenantName = tenantName;
            if (TryGetOverrideString(root, out var tenantEmail, "tenantEmail", "tenant_email"))
                fields.TenantEmail = tenantEmail;
            if (TryGetOverrideString(root, out var tenantPhone, "tenantPhone", "tenant_phone"))
                fields.TenantPhone = tenantPhone;
            if (TryGetOverrideString(root, out var tenantEmergency, "tenantEmergencyContact", "tenant_emergency_contact", "emergencyContact", "emergency_contact"))
                fields.TenantEmergencyContact = tenantEmergency;
            // Leased-premises corrections remain review suggestions. Canonical confirmation still
            // requires explicit existing Property/Unit ids and never creates physical inventory.
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
            if (TryGetOverrideNullableInt(root, out var propertyId, "propertyId", "property_id"))
                fields.PropertyId = propertyId is > 0 ? propertyId : null;
            if (TryGetOverrideNullableInt(root, out var unitId, "unitId", "unit_id"))
                fields.UnitId = unitId is > 0 ? unitId : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse application overridesJson; skipping overrides.");
        }
    }

    // -------------------------------------------------------------------------
    // Loan extraction helpers (scan a mortgage statement / closing disclosure)
    // -------------------------------------------------------------------------

    private LoanDraftFields BuildLoanFields(string? extractedFieldsJson)
    {
        var fields = new LoanDraftFields();
        if (string.IsNullOrWhiteSpace(extractedFieldsJson))
            return fields;

        try
        {
            using var doc = JsonDocument.Parse(extractedFieldsJson);
            var root = doc.RootElement;

            fields.PropertyId = ParseIntField(root, "property_id") ?? ParseIntField(root, "propertyId") ?? 0;
            fields.Lender = ReadFieldValue(root, "lender");
            fields.OriginalAmount = ParseDecimalField(root, "original_amount") ?? ParseDecimalField(root, "originalAmount");
            fields.CurrentBalance = ParseDecimalField(root, "current_balance") ?? ParseDecimalField(root, "currentBalance");
            fields.AnnualInterestRatePct = ParseDecimalField(root, "annual_interest_rate_pct") ?? ParseDecimalField(root, "annualInterestRatePct");
            fields.TermMonths = ParseIntField(root, "term_months") ?? ParseIntField(root, "termMonths");
            fields.StartDate = ParseDateField(root, "start_date") ?? ParseDateField(root, "startDate");
            fields.DayOfMonthDue = ParseIntField(root, "day_of_month_due") ?? ParseIntField(root, "dayOfMonthDue");
            fields.MonthlyPrincipalInterest = ParseDecimalField(root, "monthly_principal_interest") ?? ParseDecimalField(root, "monthlyPrincipalInterest");
            fields.MonthlyEscrow = ParseDecimalField(root, "monthly_escrow") ?? ParseDecimalField(root, "monthlyEscrow");
            fields.EscrowCoversTaxes = ParseBoolField(root, "escrow_covers_taxes") ?? ParseBoolField(root, "escrowCoversTaxes");
            fields.EscrowCoversInsurance = ParseBoolField(root, "escrow_covers_insurance") ?? ParseBoolField(root, "escrowCoversInsurance");
            fields.Notes = ReadFieldValue(root, "notes");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse loan extraction JSON.");
        }

        return fields;
    }

    /// <summary>
    /// Drops the LLM-suggested property id when it doesn't belong to this portfolio (IDOR-safe) — same
    /// pattern as the lease/application paths. A bad id falls back to 0 so the reviewer's property
    /// selection (from the scan-context deep-link) is required instead of linking a foreign property.
    /// </summary>
    private async Task ValidateLoanIdsInPortfolioAsync(
        int portfolioId, LoanDraftFields fields, CancellationToken ct)
    {
        if (fields.PropertyId > 0 &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, fields.PropertyId, ct))
        {
            fields.PropertyId = 0;
        }
    }

    private void ApplyLoanOverrides(LoanDraftFields fields, string overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(overridesJson);
            var root = doc.RootElement;

            // The review UI sends the property the landlord picked (the scan-context deep-link's
            // propertyId) — it wins and is re-validated in-portfolio by the caller.
            if (TryGetOverrideNullableInt(root, out var propertyId, "propertyId", "property_id"))
                fields.PropertyId = propertyId.GetValueOrDefault();
            if (TryGetOverrideString(root, out var lender, "lender"))
                fields.Lender = lender;
            if (TryGetOverrideDecimal(root, out var original, "originalAmount", "original_amount"))
                fields.OriginalAmount = original;
            if (TryGetOverrideDecimal(root, out var balance, "currentBalance", "current_balance"))
                fields.CurrentBalance = balance;
            if (TryGetOverrideDecimal(root, out var rate, "annualInterestRatePct", "annual_interest_rate_pct"))
                fields.AnnualInterestRatePct = rate;
            if (TryGetOverrideInt(root, out var term, "termMonths", "term_months"))
                fields.TermMonths = term;
            if (TryGetOverrideString(root, out var startStr, "startDate", "start_date") &&
                DateTime.TryParse(startStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var start))
            {
                fields.StartDate = start;
            }
            if (TryGetOverrideInt(root, out var dueDay, "dayOfMonthDue", "day_of_month_due"))
                fields.DayOfMonthDue = dueDay;
            if (TryGetOverrideDecimal(root, out var pi, "monthlyPrincipalInterest", "monthly_principal_interest"))
                fields.MonthlyPrincipalInterest = pi;
            if (TryGetOverrideDecimal(root, out var escrow, "monthlyEscrow", "monthly_escrow"))
                fields.MonthlyEscrow = escrow;
            if (TryGetOverrideBool(root, out var coversTaxes, "escrowCoversTaxes", "escrow_covers_taxes"))
                fields.EscrowCoversTaxes = coversTaxes;
            if (TryGetOverrideBool(root, out var coversInsurance, "escrowCoversInsurance", "escrow_covers_insurance"))
                fields.EscrowCoversInsurance = coversInsurance;
            if (TryGetOverrideString(root, out var notes, "notes"))
                fields.Notes = notes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse loan overridesJson; skipping overrides.");
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

    /// <summary>
    /// Parses a boolean from a field's "value" sub-property. Accepts true/false plus the common
    /// yes/no/1/0 spellings a model may emit. Returns null when absent or unrecognized.
    /// </summary>
    private static bool? ParseBoolField(JsonElement root, string key)
    {
        var str = ReadFieldValue(root, key);
        if (string.IsNullOrWhiteSpace(str))
            return null;

        var normalized = str.Trim().ToLowerInvariant();
        return normalized switch
        {
            "true" or "yes" or "y" or "1" => true,
            "false" or "no" or "n" or "0" => false,
            _ => null,
        };
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

    /// <summary>First present key wins. Accepts a JSON boolean or a true/false/yes/no/1/0 string.</summary>
    private static bool TryGetOverrideBool(JsonElement root, out bool value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var el))
                continue;

            if (el.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (el.ValueKind == JsonValueKind.False) { value = false; return true; }
            if (el.ValueKind == JsonValueKind.String)
            {
                var normalized = (el.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                switch (normalized)
                {
                    case "true" or "yes" or "y" or "1": value = true; return true;
                    case "false" or "no" or "n" or "0": value = false; return true;
                }
            }
        }
        value = false;
        return false;
    }

    /// <summary>First present key wins. Accepts null, empty string, a JSON integer, or a numeric string.</summary>
    private static bool TryGetOverrideNullableInt(JsonElement root, out int? value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var el))
                continue;

            if (el.ValueKind == JsonValueKind.Null)
            {
                value = null;
                return true;
            }

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var number))
            {
                value = number;
                return true;
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                var raw = el.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    value = null;
                    return true;
                }

                if (int.TryParse(raw, out var parsed))
                {
                    value = parsed;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    private sealed class WorkOrderDraftFields
    {
        public int PropertyId { get; set; }
        public int? UnitId { get; set; }
        public int? TenantId { get; set; }
        public int? LeaseManagementId { get; set; }
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
        public string? TenantEmail { get; set; }
        public string? TenantPhone { get; set; }
        public string? TenantEmergencyContact { get; set; }

        // Leased-premises text extracted straight off the document, used to suggest an existing
        // Property/Unit when the grounded ids were absent or uncertain.
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

    private sealed class LoanDraftFields
    {
        // The mortgaged property. Grounded id (validated in-portfolio) or, in practice, supplied by the
        // scan-context deep-link the landlord launched the scan from. Required on confirm.
        public int PropertyId { get; set; }

        public string? Lender { get; set; }
        public decimal? OriginalAmount { get; set; }
        public decimal? CurrentBalance { get; set; }
        public decimal? AnnualInterestRatePct { get; set; }
        public int? TermMonths { get; set; }
        public DateTime? StartDate { get; set; }
        public int? DayOfMonthDue { get; set; }
        public decimal? MonthlyPrincipalInterest { get; set; }
        public decimal? MonthlyEscrow { get; set; }
        public bool? EscrowCoversTaxes { get; set; }
        public bool? EscrowCoversInsurance { get; set; }
        public string? Notes { get; set; }
    }
}
