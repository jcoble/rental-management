using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.DTOs;

/// <summary>One extracted field surfaced to the review UI.</summary>
public sealed record ScanFieldDto(string Name, string Value, decimal Confidence);

public sealed record ScanCaptureContextDto(
    string? Experience,
    int? AccessContextId,
    long? AccessRevision,
    int? PropertyId,
    int? UnitId,
    int? LeaseManagementId,
    int? LeaseAgreementId,
    int? TenantAccountId,
    long? TenantLedgerEntryId,
    int? WorkOrderId,
    int? ApplicationId,
    int? RentalListingId,
    string? SourceLabel);

/// <summary>Draft as seen by the review page.</summary>
public sealed record ScanDraftResponse(
    int Id, int PortfolioId, string TargetEntityType, string Status,
    string FileUrl, IReadOnlyList<ScanFieldDto> Fields,
    string? ModelId, int? TokensUsed, decimal? CostUsd, string? FailureReason,
    DateTime CreatedAt, DateTime? ReviewedAt, DateTime? ConfirmedAt,
    string? CreatedEntityType = null, long? CreatedEntityId = null, int? CreatedUnitId = null,
    // Conversational-voice ("Tell me") slot state. Populated only via
    // WithVoiceSlots() for the voice endpoints; left at complete/empty defaults
    // for scanned documents (which the scan review screen never reads).
    IReadOnlyList<string>? MissingRequired = null,
    string? NextPrompt = null,
    bool Complete = true,
    bool Ambiguous = false,
    // Lease-import preview: for a lease draft, whether the Property/Unit resolved or still needs
    // an explicit selection. Null for non-lease drafts. Populated by the controller via
    // WithLeaseProposal() so the review UI can show + let the user correct before committing.
    LeaseImportProposal? LeaseProposal = null,
    ScanCaptureContextDto? CaptureContext = null,
    int? SourceStoredFileId = null,
    string? SourceContentSha256 = null)
{
    /// <summary>Returns a copy carrying the lease-import property/unit proposal for the review UI.</summary>
    public ScanDraftResponse WithLeaseProposal(LeaseImportProposal? proposal) =>
        this with { LeaseProposal = proposal };

    /// <summary>
    /// Builds a <see cref="ScanDraftResponse"/> from a <see cref="ScanDraft"/> entity,
    /// deserializing <see cref="ScanDraft.ExtractedFields"/> JSON
    /// <c>{name:{value,confidence}}</c> into the <see cref="Fields"/> list.
    /// Returns an empty list when <paramref name="d"/>.ExtractedFields is null or Status is Pending.
    /// </summary>
    public static ScanDraftResponse FromEntity(
        ScanDraft d,
        string? createdEntityType = null,
        long? createdEntityId = null,
        int? createdUnitId = null)
    {
        var fields = ParseFields(d.ExtractedFields);
        var fileUrl = $"/api/v1/scans/{d.Id}/file";

        return new ScanDraftResponse(
            d.Id, d.PortfolioId, d.TargetEntityType, d.Status,
            fileUrl, fields,
            d.ModelId, d.TokensUsed, d.CostUsd, d.FailureReason,
            d.CreatedAt, d.ReviewedAt, d.ConfirmedAt,
            createdEntityType, createdEntityId, createdUnitId,
            CaptureContext: ToCaptureContext(d),
            SourceStoredFileId: d.SourceStoredFileId,
            SourceContentSha256: d.SourceContentSha256);
    }

    internal static ScanCaptureContextDto ToCaptureContext(ScanDraft d) => new(
        d.CaptureExperience?.ToString(), d.CaptureAccessContextId, d.CaptureAccessRevision,
        d.CapturePropertyId, d.CaptureUnitId, d.CaptureLeaseManagementId,
        d.CaptureLeaseAgreementId, d.CaptureTenantAccountId, d.CaptureTenantLedgerEntryId,
        d.CaptureWorkOrderId, d.CaptureApplicationId, d.CaptureRentalListingId,
        d.SourceLabel);

    /// <summary>
    /// Returns a copy with the conversational-voice slot fields
    /// (<see cref="MissingRequired"/>, <see cref="NextPrompt"/>,
    /// <see cref="Complete"/>) computed from this draft's own fields and target
    /// type via <see cref="VoiceSlots"/>. Used by the "Tell me" voice flow; the
    /// scan flow leaves them at their complete/empty defaults.
    /// </summary>
    public ScanDraftResponse WithVoiceSlots()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            values[field.Name] = field.Value;
        }

        var eval = VoiceSlots.Evaluate(TargetEntityType, values);
        return this with
        {
            MissingRequired = eval.Missing,
            NextPrompt = eval.NextPrompt,
            Complete = eval.Complete,
            Ambiguous = eval.Ambiguous,
        };
    }

    internal static IReadOnlyList<ScanFieldDto> ParseFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return [];

            var result = new List<ScanFieldDto>();

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var valueEl = prop.Value.TryGetProperty("value", out var v) ? v : default;
                var confEl = prop.Value.TryGetProperty("confidence", out var c) ? c : default;

                var value = valueEl.ValueKind == JsonValueKind.String
                    ? valueEl.GetString() ?? string.Empty
                    : valueEl.ValueKind != JsonValueKind.Undefined
                        ? valueEl.ToString()
                        : string.Empty;

                var confidence = confEl.ValueKind == JsonValueKind.Number &&
                                 confEl.TryGetDecimal(out var confDec)
                    ? confDec
                    : 0m;

                result.Add(new ScanFieldDto(prop.Name, value, confidence));
            }

            return result;
        }
        catch
        {
            return [];
        }
    }
}

public sealed record ScanCreatedResponse(int DraftId, string Status, string FileUrl);

public sealed record ScanDraftListResponse(
    IReadOnlyList<ScanDraftResponse> Items,
    int TotalCount,
    int Skip,
    int Take);

/// <summary>Response from a bulk-scan batch upload: the created batch + the ids of its drafts.</summary>
public sealed record ScanBatchCreatedResponse(
    int BatchId, string? Name, string TargetEntityType, string Status, int FileCount,
    IReadOnlyList<int> DraftIds);

/// <summary>Rollup counts for a batch's drafts, by status.</summary>
public sealed record ScanBatchCounts(
    int Total, int Pending, int Reviewing, int Confirmed, int Rejected, int Failed);

/// <summary>A batch in the batch list, with its draft rollup counts.</summary>
public sealed record ScanBatchSummaryResponse(
    int Id, string? Name, string TargetEntityType, string Status, int FileCount,
    DateTime CreatedAtUtc, ScanBatchCounts Counts);

/// <summary>One draft in a batch's review queue: id, status, and a short extracted summary.</summary>
public sealed record ScanBatchDraftResponse(
    int Id, string Status, string TargetEntityType, string FileUrl,
    string? Tenant, string? Unit, string? Term,
    int? CreatedEntityId, DateTime CreatedAt, string? FailureReason = null);

/// <summary>Full batch detail: the batch, its rollup counts, and its drafts for the review queue.</summary>
public sealed record ScanBatchDetailResponse(
    int Id, string? Name, string TargetEntityType, string Status, int FileCount,
    DateTime CreatedAtUtc, ScanBatchCounts Counts, IReadOnlyList<ScanBatchDraftResponse> Drafts,
    int DraftTotalCount, int Skip, int Take);

/// <summary>Stable operation identity plus optional reviewed field overrides for scan confirmation.</summary>
public sealed class ConfirmScanRequest
{
    [Required]
    [MaxLength(160)]
    [RegularExpression(@".*\S.*", ErrorMessage = "ClientOperationId cannot be blank.")]
    public string ClientOperationId { get; set; } = string.Empty;

    public string? OverridesJson { get; set; }
}

/// <summary>Reviewed Guided Setup lease facts admitted through the canonical scan confirmer.</summary>
public sealed class CreateManualLeaseRequest
{
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? TenantEmail { get; set; }
    public string? TenantPhone { get; set; }
    public string? TenantEmergencyContact { get; set; }
    public string? PropertyName { get; set; }
    public string? PropertyType { get; set; }
    public RentalStructure? RentalStructure { get; set; }
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
    public int TermsSchemaVersion { get; set; } = 1;
    public string? TermsPayload { get; set; }
    public short GracePeriodDays { get; set; }
    public DateTime? PossessionGivenAtUtc { get; set; }
    public RentTrackingStartMode RentTrackingStartMode { get; set; } = RentTrackingStartMode.ForwardOnly;
    public DateOnly? RentTrackingStartOn { get; set; }
}

/// <summary>Stable operation identity plus selected canonical account for payment scan review.</summary>
public sealed class SetScanDraftPaymentAccountRequest
{
    [Range(1, int.MaxValue)]
    public int TenantAccountId { get; set; }

    [Required]
    [MaxLength(160)]
    [RegularExpression(@".*\S.*", ErrorMessage = "ClientOperationId cannot be blank.")]
    public string ClientOperationId { get; set; } = string.Empty;
}

/// <summary>Optional rejection reason when rejecting a scan draft.</summary>
public sealed class RejectScanRequest
{
    [MaxLength(1000)]
    public string? Reason { get; set; }
}
