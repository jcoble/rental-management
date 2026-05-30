namespace RentalCommand.Core.Entities;

public class Vendor
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? TaxId { get; set; }
    public bool Is1099Eligible { get; set; }
    public bool W9OnFile { get; set; }
    public bool Preferred { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];
}
