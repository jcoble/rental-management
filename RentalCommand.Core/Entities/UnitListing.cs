using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Manual rental-listing packet for a unit. V1 prepares Zillow-ready content and tracks the
/// landlord's manual Zillow Rental Manager status; it does not call Zillow APIs or scrape Zillow.
/// </summary>
public class UnitListing : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }

    public UnitListingChannel Channel { get; set; } = UnitListingChannel.ZillowManual;
    public UnitListingStatus Status { get; set; } = UnitListingStatus.Draft;

    public string Headline { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Rent { get; set; }
    public decimal? SecurityDeposit { get; set; }
    public decimal Bedrooms { get; set; }
    public decimal Bathrooms { get; set; }
    public int? SquareFeet { get; set; }
    public DateTime? AvailableOn { get; set; }

    public string? LeaseTerms { get; set; }
    public string? PetPolicy { get; set; }
    public string? Utilities { get; set; }
    public string? Parking { get; set; }
    public string? Amenities { get; set; }
    public string? PhotoNotes { get; set; }

    public string? ZillowListingUrl { get; set; }
    public string? ZillowApplicationUrl { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
}
