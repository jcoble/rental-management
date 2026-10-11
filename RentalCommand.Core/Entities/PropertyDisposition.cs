using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class PropertyDisposition : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public DateTime ClosedOnDate { get; set; }
    public decimal SalePrice { get; set; }
    public decimal SellingCosts { get; set; }
    public string? BuyerName { get; set; }
    public string? Memo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
}
