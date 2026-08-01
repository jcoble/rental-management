using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class Appointment : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? LeaseManagementId { get; set; }
    public int? RentalApplicationId { get; set; }
    public int? TenantId { get; set; }
    public int? WorkOrderId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? ProspectName { get; set; }
    public string? ProspectEmail { get; set; }
    public AppointmentType Type { get; set; } = AppointmentType.Showing;
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;
    public DateTime ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public string? AssignedTo { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public LeaseManagement? LeaseManagement { get; set; }
    public RentalApplication? RentalApplication { get; set; }
    public Tenant? Tenant { get; set; }
    public WorkOrder? WorkOrder { get; set; }
}
