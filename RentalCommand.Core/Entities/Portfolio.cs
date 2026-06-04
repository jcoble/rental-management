using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class Portfolio
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ManagementCompanyName { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "America/New_York";
    public PortfolioStatus Status { get; set; } = PortfolioStatus.Active;
    public string Currency { get; set; } = "USD";
    public string? Settings { get; set; }

    /// <summary>
    /// Opaque token embedded in the public, no-login rental-application link. Generated/rotated on
    /// demand by the landlord. A null token means public applications are not currently accepted.
    /// Public endpoints resolve the portfolio by this token only — never by a client-supplied id.
    /// </summary>
    public string? PublicApplicationToken { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public List<Property> Properties { get; set; } = [];
    public List<Owner> Owners { get; set; } = [];
    public List<OwnerEntity> OwnerEntities { get; set; } = [];
    public List<Tenant> Tenants { get; set; } = [];
    public List<Vendor> Vendors { get; set; } = [];
    public List<Lease> Leases { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<RecurringMaintenanceTask> RecurringMaintenanceTasks { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];
    public List<Inspection> Inspections { get; set; } = [];
    public List<ActivityLog> Activities { get; set; } = [];
    public List<UserAccount> UserAccounts { get; set; } = [];
    public List<PortalMessage> PortalMessages { get; set; } = [];
    public List<RentalApplication> RentalApplications { get; set; } = [];
}
