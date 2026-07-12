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
    public string? Employer { get; set; }
    public decimal? MonthlyIncome { get; set; }

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

    public DateTime SubmittedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }

    /// <summary>Tenant created when this application was approved; null otherwise.</summary>
    public int? ApprovedTenantId { get; set; }

    /// <summary>
    /// The one lease relationship prepared from this approved application. This is a durable
    /// conversion result, not a legacy Lease bridge; possession and portal access are separate
    /// explicit commands.
    /// </summary>
    public int? PreparedLeaseManagementId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Tenant? ApprovedTenant { get; set; }
    public LeaseManagement? PreparedLeaseManagement { get; set; }
}
