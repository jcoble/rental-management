using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class Portfolio
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ManagementCompanyName { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "America/New_York";
    public PortfolioStatus Status { get; set; } = PortfolioStatus.Active;
    public string? Settings { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<Property> Properties { get; set; } = [];
    public List<Owner> Owners { get; set; } = [];
    public List<Tenant> Tenants { get; set; } = [];
    public List<Vendor> Vendors { get; set; } = [];
    public List<Lease> Leases { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];
    public List<Inspection> Inspections { get; set; } = [];
    public List<ActivityLog> Activities { get; set; } = [];
    public List<UserAccount> UserAccounts { get; set; } = [];
    public List<PortalMessage> PortalMessages { get; set; } = [];
}
