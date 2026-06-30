using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public class UnitListingResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
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
    public bool IsPosted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static UnitListingResponse FromEntity(UnitListing e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        UnitId = e.UnitId,
        Channel = e.Channel.ToString(),
        Status = e.Status.ToString(),
        Headline = e.Headline,
        Description = e.Description,
        Rent = e.Rent,
        SecurityDeposit = e.SecurityDeposit,
        Bedrooms = e.Bedrooms,
        Bathrooms = e.Bathrooms,
        SquareFeet = e.SquareFeet,
        AvailableOn = e.AvailableOn,
        LeaseTerms = e.LeaseTerms,
        PetPolicy = e.PetPolicy,
        Utilities = e.Utilities,
        Parking = e.Parking,
        Amenities = e.Amenities,
        PhotoNotes = e.PhotoNotes,
        ZillowListingUrl = e.ZillowListingUrl,
        ZillowApplicationUrl = e.ZillowApplicationUrl,
        PostedAtUtc = e.PostedAtUtc,
        IsPosted = e.Status == UnitListingStatus.Posted,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class SaveUnitListingRequest
{
    [MaxLength(40)]
    public string? Status { get; set; }

    [MaxLength(200)]
    public string? Headline { get; set; }

    [MaxLength(4000)]
    public string? Description { get; set; }

    [Range(0, 99999999)]
    public decimal? Rent { get; set; }

    [Range(0, 99999999)]
    public decimal? SecurityDeposit { get; set; }

    [Range(0, 99)]
    public decimal? Bedrooms { get; set; }

    [Range(0, 99)]
    public decimal? Bathrooms { get; set; }

    [Range(0, 99999)]
    public int? SquareFeet { get; set; }

    public DateTime? AvailableOn { get; set; }

    [MaxLength(1000)]
    public string? LeaseTerms { get; set; }

    [MaxLength(1000)]
    public string? PetPolicy { get; set; }

    [MaxLength(1000)]
    public string? Utilities { get; set; }

    [MaxLength(1000)]
    public string? Parking { get; set; }

    [MaxLength(2000)]
    public string? Amenities { get; set; }

    [MaxLength(2000)]
    public string? PhotoNotes { get; set; }

    [MaxLength(1000)]
    public string? ZillowListingUrl { get; set; }

    [MaxLength(1000)]
    public string? ZillowApplicationUrl { get; set; }

    public DateTime? PostedAtUtc { get; set; }
}
