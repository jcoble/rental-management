using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class Unit
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public string? FloorPlan { get; set; }
    public decimal Bedrooms { get; set; }
    public decimal Bathrooms { get; set; }
    public int? SquareFeet { get; set; }
    public decimal MarketRent { get; set; }
    public UnitStatus Status { get; set; } = UnitStatus.Vacant;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Property? Property { get; set; }
    public List<Lease> Leases { get; set; } = [];
    public List<UnitListing> UnitListings { get; set; } = [];
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];
    public List<Inspection> Inspections { get; set; } = [];
}
