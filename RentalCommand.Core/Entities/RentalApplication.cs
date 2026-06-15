using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A no-login online rental application. An applicant opens a public link (resolved by the
/// portfolio's <see cref="Portfolio.PublicApplicationToken"/>), optionally autofills from a
/// photo of their ID / pay stub, and submits. The landlord reviews and approves/declines in-app;
/// approving creates a real <see cref="Tenant"/>.
/// </summary>
public class RentalApplication : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }

    /// <summary>Owning portfolio. Resolved server-side from the public link token — never trusted from the client.</summary>
    public int PortfolioId { get; set; }

    /// <summary>Optional property the applicant is applying for (chosen on the public form).</summary>
    public int? PropertyId { get; set; }

    /// <summary>Optional unit the applicant is applying for.</summary>
    public int? UnitId { get; set; }

    // --- Applicant identity ---
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTime? DateOfBirth { get; set; }

    // Structured current address. CurrentAddress is kept as a legacy/composed single-line
    // form (set from these on write) so older read paths (screening summary, etc.) keep working.
    public string? CurrentAddressLine1 { get; set; }
    public string? CurrentAddressLine2 { get; set; }
    public string? CurrentCity { get; set; }
    public string? CurrentState { get; set; }
    public string? CurrentPostalCode { get; set; }
    public string? CurrentAddress { get; set; }

    // --- Employment / income ---
    // The single Employer/MonthlyIncome columns remain the source of truth for list views, screening,
    // and the tenant note. When the configurable form collects multiple income sources, the FIRST one
    // is mirrored into these two columns on submit so every existing read path keeps working unchanged.
    public string? Employer { get; set; }
    public decimal? MonthlyIncome { get; set; }

    /// <summary>
    /// Multiple employer/income entries from the configurable form, as a JSON array of
    /// <c>{ "employer": "...", "monthlyIncome": 1234.56 }</c> (Postgres jsonb). When present this is the
    /// full record of the applicant's income; the first entry is also mirrored into
    /// <see cref="Employer"/>/<see cref="MonthlyIncome"/>. Null when the form used the single-income shape.
    /// </summary>
    public string? IncomeSourcesJson { get; set; }

    /// <summary>
    /// Pet information from the configurable form, as a JSON object
    /// <c>{ "hasPets": true, "pets": [ { "type":"Dog", "name":"Rex", "breed":"Lab", "weight":"60" } ] }</c>
    /// (Postgres jsonb). Null/absent when the pets section is off or unanswered.
    /// </summary>
    public string? PetsJson { get; set; }

    /// <summary>
    /// Answers to the landlord's custom questions, as a JSON object mapping a custom-field <c>id</c> to
    /// its answer (string for text/select, number for number, boolean for yes/no) (Postgres jsonb).
    /// Only ids present in the portfolio's config at submit time are kept; unknown keys are dropped.
    /// </summary>
    public string? CustomFieldAnswersJson { get; set; }

    public DateTime? DesiredMoveInDate { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// JSON provenance from the photo-ID / pay-stub autofill: per-field {value, confidence}
    /// captured at submit time so the landlord can see which fields were machine-extracted.
    /// </summary>
    public string? IdExtractedFields { get; set; }

    // --- FCRA consent (seam for the later paid screening wave) ---

    /// <summary>Whether the applicant gave explicit consent to a background/credit screening (FCRA).</summary>
    public bool ConsentGiven { get; set; }

    /// <summary>When consent was captured (UTC); null when not given.</summary>
    public DateTime? ConsentAtUtc { get; set; }

    /// <summary>Originating IP address recorded with the consent, for the FCRA audit trail.</summary>
    public string? ConsentIpAddress { get; set; }

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Submitted;

    /// <summary>Optional reason captured when the application is declined.</summary>
    public string? DecisionReason { get; set; }

    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAtUtc { get; set; }

    /// <summary>Tenant created when this application was approved; null otherwise.</summary>
    public int? ApprovedTenantId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Tenant? ApprovedTenant { get; set; }
}
