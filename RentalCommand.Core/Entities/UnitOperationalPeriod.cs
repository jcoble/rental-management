using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>A non-occupancy operational condition affecting a Unit for a period.</summary>
public class UnitOperationalPeriod : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public UnitOperationalPeriodType Type { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public int? SourceLeaseManagementId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public LeaseManagement? SourceLeaseManagement { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
}
