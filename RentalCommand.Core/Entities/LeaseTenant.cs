using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class LeaseTenant : IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public int TenantId { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Lease? Lease { get; set; }
    public Tenant? Tenant { get; set; }
}
