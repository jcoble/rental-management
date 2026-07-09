using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class Lease : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public int TenantId { get; set; }
    public string LeaseNumber { get; set; } = string.Empty;
    public LeaseStatus Status { get; set; } = LeaseStatus.Draft;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime? MoveInDate { get; set; }
    public DateTime? MoveOutDate { get; set; }
    public decimal MonthlyRent { get; set; }
    public decimal SecurityDeposit { get; set; }
    public decimal LateFeeAmount { get; set; }
    public int RentDueDay { get; set; } = 1;
    public DateTime? RentTrackingStartDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>When a lease-expiry reminder was last sent (idempotency for the reminder worker).</summary>
    public DateTime? ExpiryReminderSentAt { get; set; }

    // --- Electronic signature (e-sign) state ---

    /// <summary>Provider-side signature request / envelope id once the agreement has been sent out. Null until sent.</summary>
    public string? EsignEnvelopeId { get; set; }

    /// <summary>Where this lease sits in the e-sign workflow. <see cref="EsignStatus.None"/> until a request is sent.</summary>
    public EsignStatus EsignStatus { get; set; } = EsignStatus.None;

    /// <summary>The <see cref="StoredFile"/> id of the fully-signed agreement PDF, set when the provider reports "signed".</summary>
    public int? SignedDocumentStoredFileId { get; set; }

    /// <summary>
    /// Reusable document template used to render this lease agreement. Null means the legacy built-in
    /// QuestPDF generator was used.
    /// </summary>
    public int? DocumentTemplateId { get; set; }

    /// <summary>Template version frozen when the agreement is rendered/sent.</summary>
    public int? DocumentTemplateVersion { get; set; }

    /// <summary>
    /// JSON object (stored as jsonb) holding the full scan extraction superset for leases imported
    /// from a scanned PDF. Null for manually entered leases.
    /// </summary>
    public string? ExtractedData { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Tenant? Tenant { get; set; }
    public List<LeaseTenant> LeaseTenants { get; set; } = [];
    public DocumentTemplate? DocumentTemplate { get; set; }
    public List<Payment> Payments { get; set; } = [];
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];
    public List<Inspection> Inspections { get; set; } = [];
    public List<EvictionCase> EvictionCases { get; set; } = [];
}
