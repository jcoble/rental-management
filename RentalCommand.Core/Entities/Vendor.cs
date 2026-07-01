using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class Vendor : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? TaxId { get; set; }
    public string? AddressLine1 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public bool Is1099Eligible { get; set; }
    public bool W9OnFile { get; set; }
    public bool Preferred { get; set; }
    public string? Notes { get; set; }

    /// <summary>Cached average of all <see cref="VendorRating.Stars"/> (1–5); null until first rated.</summary>
    public decimal? AverageRating { get; set; }

    /// <summary>Cached number of ratings that make up <see cref="AverageRating"/>.</summary>
    public int RatingCount { get; set; }

    /// <summary>Cached count of work orders this vendor has completed (incl. via DONE replies).</summary>
    public int JobsCompleted { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];
    public List<VendorDispatch> Dispatches { get; set; } = [];
    public List<VendorRating> Ratings { get; set; } = [];
}
