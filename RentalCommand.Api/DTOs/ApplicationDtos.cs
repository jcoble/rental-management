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

/// <summary>Wire shape returned for a <see cref="RentalApplication"/> to the landlord.</summary>
public class ApplicationResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTime? DateOfBirth { get; set; }
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

    /// <summary>Stable selector for frontend tests, e.g. <c>application-1</c>.</summary>
    public string TestId => $"application-{Id}";

    public static ApplicationResponse FromEntity(RentalApplication e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        UnitId = e.UnitId,
        FirstName = e.FirstName,
        LastName = e.LastName,
        Email = e.Email,
        Phone = e.Phone,
        DateOfBirth = e.DateOfBirth,
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

/// <summary>Body for <c>POST /api/v1/applications/{id}/decline</c>.</summary>
public class DeclineApplicationRequest
{
    [MaxLength(1000)]
    public string? Reason { get; set; }
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
