using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

// ---------------------------------------------------------------------------
// Public (no-login) shapes
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal, non-sensitive portfolio info returned for the public application form so the applicant
/// knows who they're applying to and can optionally pick a property. No portfolio internals leak.
/// </summary>
public class PublicApplicationFormInfo
{
    public string ManagementCompanyName { get; set; } = string.Empty;
    public IReadOnlyList<PublicPropertyOption> Properties { get; set; } = [];
}

/// <summary>A single property the applicant may choose on the public form.</summary>
public class PublicPropertyOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;

    public IReadOnlyList<PublicUnitOption> Units { get; set; } = [];
}

/// <summary>A single rentable unit option under a property on the public form.</summary>
public class PublicUnitOption
{
    public int Id { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public UnitStatus Status { get; set; }
}

/// <summary>
/// Body an anonymous applicant submits via <c>POST /api/v1/public/applications/{token}</c>.
/// PortfolioId is never accepted here — it is resolved server-side from the link token.
/// </summary>
public class SubmitApplicationRequest
{
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(200)]
    [EmailAddress]
    public string? Email { get; set; }

    [MaxLength(50)]
    [Phone]
    public string? Phone { get; set; }

    public DateTime? DateOfBirth { get; set; }

    [MaxLength(250)]
    public string? CurrentAddressLine1 { get; set; }

    [MaxLength(250)]
    public string? CurrentAddressLine2 { get; set; }

    [MaxLength(120)]
    public string? CurrentCity { get; set; }

    [MaxLength(60)]
    public string? CurrentState { get; set; }

    [MaxLength(20)]
    public string? CurrentPostalCode { get; set; }

    [MaxLength(500)]
    public string? CurrentAddress { get; set; }

    [MaxLength(200)]
    public string? Employer { get; set; }

    [Range(0, 100_000_000)]
    public decimal? MonthlyIncome { get; set; }

    public DateTime? DesiredMoveInDate { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Optional autofill provenance from the photo-ID/pay-stub scan: a JSON object of
    /// per-field {value, confidence}. Stored as-is so the landlord can see what was machine-filled.
    /// </summary>
    [MaxLength(8000)]
    public string? IdExtractedFields { get; set; }

    /// <summary>
    /// FCRA consent: the applicant must agree to a future background/credit screening. Required to
    /// be <c>true</c>; the server records the timestamp and originating IP.
    /// </summary>
    [Required]
    public bool ConsentGiven { get; set; }
}

/// <summary>Thank-you / reference returned after a successful public submit.</summary>
public class SubmitApplicationResult
{
    public int ApplicationId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = "Thank you. Your application has been received.";
}

/// <summary>One machine-extracted prefill field with its confidence (0.0–1.0).</summary>
public class PrefillField
{
    public string Value { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
}

/// <summary>
/// Result of <c>POST /api/v1/public/applications/{token}/scan-id</c>: prefill fields keyed by the
/// application field name. Empty when no LLM key is configured (the applicant types manually).
/// </summary>
public class ScanIdResult
{
    /// <summary>Prefill fields keyed by application field name (firstName, lastName, …).</summary>
    public Dictionary<string, PrefillField> Fields { get; set; } = new();

    /// <summary>True when extraction actually ran (a provider was configured and returned values).</summary>
    public bool Extracted { get; set; }
}

// ---------------------------------------------------------------------------
// Authed (landlord) shapes
// ---------------------------------------------------------------------------

/// <summary>
/// Body for an authed, portfolio-scoped application create — used by the scan-IN confirm path when a
/// landlord scans a completed paper rental application. Same applicant shape as
/// <see cref="SubmitApplicationRequest"/>, minus the public-form FCRA consent (the landlord is keying a
/// paper application, not the applicant consenting in-app). PortfolioId is taken from the caller's JWT.
/// </summary>
public class CreateApplicationRequest
{
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    public DateTime? DateOfBirth { get; set; }

    [MaxLength(500)]
    public string? CurrentAddress { get; set; }

    [MaxLength(200)]
    public string? Employer { get; set; }

    [Range(0, 100_000_000)]
    public decimal? MonthlyIncome { get; set; }

    public DateTime? DesiredMoveInDate { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Optional extraction provenance from the scan (the per-field {value, confidence} JSON) so the
    /// landlord can see which fields were machine-extracted. Stored as-is.
    /// </summary>
    [MaxLength(8000)]
    public string? IdExtractedFields { get; set; }
}

/// <summary>
/// Landlord correction body for a submitted/under-review application. Nullable fields are partial:
/// omitted means "leave as-is"; empty strings clear optional text fields. Explicit clear flags are
/// used for nullable dates/numbers/foreign keys where JSON null is otherwise ambiguous.
/// </summary>
public class UpdateApplicationRequest
{
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    public bool ClearProperty { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    public bool ClearUnit { get; set; }

    [MaxLength(100)]
    public string? FirstName { get; set; }

    [MaxLength(100)]
    public string? LastName { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public string? Email { get; set; }

    [MaxLength(50)]
    [Phone]
    public string? Phone { get; set; }

    public DateTime? DateOfBirth { get; set; }
    public bool ClearDateOfBirth { get; set; }

    [MaxLength(250)]
    public string? CurrentAddressLine1 { get; set; }

    [MaxLength(250)]
    public string? CurrentAddressLine2 { get; set; }

    [MaxLength(120)]
    public string? CurrentCity { get; set; }

    [MaxLength(60)]
    public string? CurrentState { get; set; }

    [MaxLength(20)]
    public string? CurrentPostalCode { get; set; }

    [MaxLength(500)]
    public string? CurrentAddress { get; set; }

    [MaxLength(200)]
    public string? Employer { get; set; }

    [Range(0, 100_000_000)]
    public decimal? MonthlyIncome { get; set; }
    public bool ClearMonthlyIncome { get; set; }

    public DateTime? DesiredMoveInDate { get; set; }
    public bool ClearDesiredMoveInDate { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

/// <summary>Wire shape returned for a <see cref="RentalApplication"/> to the landlord.</summary>
public class ApplicationResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public string? PropertyName { get; set; }
    public string? UnitNumber { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? CurrentAddressLine1 { get; set; }
    public string? CurrentAddressLine2 { get; set; }
    public string? CurrentCity { get; set; }
    public string? CurrentState { get; set; }
    public string? CurrentPostalCode { get; set; }
    public string? CurrentAddress { get; set; }
    public string? Employer { get; set; }
    public decimal? MonthlyIncome { get; set; }
    public DateTime? DesiredMoveInDate { get; set; }
    public string? Notes { get; set; }
    public string? IdExtractedFields { get; set; }
    public bool ConsentGiven { get; set; }
    public DateTime? ConsentAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? DecisionReason { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public int? ApprovedTenantId { get; set; }
    public bool HasScan { get; set; }
    public bool ScanIsImage { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>application-1</c>.</summary>
    public string TestId => $"application-{Id}";

    public static ApplicationResponse FromEntity(RentalApplication e, string? propertyName = null, string? unitNumber = null) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        UnitId = e.UnitId,
        PropertyName = propertyName ?? e.Property?.Name,
        UnitNumber = unitNumber ?? e.Unit?.UnitNumber,
        FirstName = e.FirstName,
        LastName = e.LastName,
        Email = e.Email,
        Phone = e.Phone,
        DateOfBirth = e.DateOfBirth,
        CurrentAddressLine1 = e.CurrentAddressLine1,
        CurrentAddressLine2 = e.CurrentAddressLine2,
        CurrentCity = e.CurrentCity,
        CurrentState = e.CurrentState,
        CurrentPostalCode = e.CurrentPostalCode,
        CurrentAddress = e.CurrentAddress,
        Employer = e.Employer,
        MonthlyIncome = e.MonthlyIncome,
        DesiredMoveInDate = e.DesiredMoveInDate,
        Notes = e.Notes,
        IdExtractedFields = e.IdExtractedFields,
        ConsentGiven = e.ConsentGiven,
        ConsentAtUtc = e.ConsentAtUtc,
        Status = e.Status.ToString(),
        DecisionReason = e.DecisionReason,
        SubmittedAtUtc = e.SubmittedAtUtc,
        ReviewedAtUtc = e.ReviewedAtUtc,
        ApprovedTenantId = e.ApprovedTenantId,
    };
}

public class ApplicationListResponse
{
    public IReadOnlyList<ApplicationResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

/// <summary>Body for <c>POST /api/v1/applications/{id}/decline</c>.</summary>
public class DeclineApplicationRequest
{
    [MaxLength(1000)]
    public string? Reason { get; set; }
}

/// <summary>
/// Records a real application/screening fee as income against an application, before any lease exists.
/// The property is taken from the application; the payment is created Paid.
/// </summary>
public class RecordApplicationFeeRequest
{
    [Range(0.01, 99999999)]
    public decimal Amount { get; set; }

    /// <summary>How the fee was paid (e.g. Card, Cash, Check); free text, optional.</summary>
    [MaxLength(100)]
    public string? Method { get; set; }

    /// <summary>When the fee was received; defaults to now when omitted.</summary>
    public DateTime? PaidDate { get; set; }
}

/// <summary>Result of approving an application: the new status plus the tenant that was created.</summary>
public class ApproveApplicationResult
{
    public int ApplicationId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TenantId { get; set; }
}

/// <summary>
/// Result of <c>POST /api/v1/applications/link</c>: the rotated public token plus the relative apply
/// path the landlord can share. The web app prefixes its own origin to form the full URL.
/// </summary>
public class ApplicationLinkResult
{
    public string Token { get; set; } = string.Empty;

    /// <summary>Relative public apply path, e.g. <c>/apply/{token}</c>.</summary>
    public string ApplyPath { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Screening (FCRA) shapes
// ---------------------------------------------------------------------------

/// <summary>Wire shape for a persisted <see cref="ScreeningResult"/> returned to the landlord.</summary>
public class ScreeningResultResponse
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CreditScoreBand { get; set; }
    public bool? HasCriminalRecord { get; set; }
    public bool? HasEvictionRecord { get; set; }
    public string? Recommendation { get; set; }
    public string? ProviderReference { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public static ScreeningResultResponse FromEntity(ScreeningResult e) => new()
    {
        Id = e.Id,
        ApplicationId = e.ApplicationId,
        Status = e.Status.ToString(),
        CreditScoreBand = e.CreditScoreBand,
        HasCriminalRecord = e.HasCriminalRecord,
        HasEvictionRecord = e.HasEvictionRecord,
        Recommendation = e.Recommendation?.ToString(),
        ProviderReference = e.ProviderReference,
        RequestedAtUtc = e.RequestedAtUtc,
        CompletedAtUtc = e.CompletedAtUtc,
    };
}

/// <summary>Body for <c>POST /api/v1/applications/{id}/adverse-action</c>.</summary>
public class GenerateAdverseActionRequest
{
    /// <summary>Stable client-generated key reused when this logical generation attempt is retried.</summary>
    [Required, MaxLength(200)]
    public string OperationKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for the principal reason printed on the notice. When omitted, the reason is
    /// derived from the application's decision reason / screening recommendation.
    /// </summary>
    [MaxLength(1000)]
    public string? Reason { get; set; }

    /// <summary>When true (default), the notice is also enqueued to the applicant's email via the outbox.</summary>
    public bool SendToApplicant { get; set; } = true;
}

/// <summary>Result of generating an FCRA adverse-action notice.</summary>
public class AdverseActionNoticeResponse
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string CreditReportingAgency { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
    public int? StoredFileId { get; set; }
    public DateTime? SentAtUtc { get; set; }

    public static AdverseActionNoticeResponse FromEntity(AdverseActionNotice e) => new()
    {
        Id = e.Id,
        ApplicationId = e.ApplicationId,
        Reason = e.Reason,
        CreditReportingAgency = e.CreditReportingAgency,
        GeneratedAtUtc = e.GeneratedAtUtc,
        StoredFileId = e.StoredFileId,
        SentAtUtc = e.SentAtUtc,
    };
}
