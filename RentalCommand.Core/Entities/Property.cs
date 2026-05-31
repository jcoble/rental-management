using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class Property
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? OwnerId { get; set; }
    public int? OwnerEntityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public PropertyType PropertyType { get; set; } = PropertyType.MultiFamily;
    public PropertyStatus Status { get; set; } = PropertyStatus.Active;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public int? YearBuilt { get; set; }
    public decimal? ManagementFeePercent { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Owner? Owner { get; set; }
    public OwnerEntity? OwnerEntity { get; set; }
    public List<Unit> Units { get; set; } = [];
    public List<Lease> Leases { get; set; } = [];
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];
    public List<Inspection> Inspections { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];
}
