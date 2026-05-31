using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class Lease
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
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>When a lease-expiry reminder was last sent (idempotency for the reminder worker).</summary>
    public DateTime? ExpiryReminderSentAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Tenant? Tenant { get; set; }
    public List<Payment> Payments { get; set; } = [];
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];
    public List<Inspection> Inspections { get; set; } = [];
}
