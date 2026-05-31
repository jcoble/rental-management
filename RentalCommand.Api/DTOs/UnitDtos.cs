using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Unit"/>.</summary>
public class UnitResponse
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public string? FloorPlan { get; set; }
    public decimal Bedrooms { get; set; }
    public decimal Bathrooms { get; set; }
    public int? SquareFeet { get; set; }
    public decimal MarketRent { get; set; }
    public UnitStatus Status { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>unit-1</c>.</summary>
    public string TestId => $"unit-{Id}";

    public static UnitResponse FromEntity(Unit e) => new()
    {
        Id = e.Id,
        PropertyId = e.PropertyId,
        UnitNumber = e.UnitNumber,
        FloorPlan = e.FloorPlan,
        Bedrooms = e.Bedrooms,
        Bathrooms = e.Bathrooms,
        SquareFeet = e.SquareFeet,
        MarketRent = e.MarketRent,
        Status = e.Status,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class CreateUnitRequest
{
    [Required]
    public int PropertyId { get; set; }

    [Required]
    [MaxLength(50)]
    public string UnitNumber { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? FloorPlan { get; set; }

    public decimal Bedrooms { get; set; }
    public decimal Bathrooms { get; set; }
    public int? SquareFeet { get; set; }
    public decimal MarketRent { get; set; }
    public UnitStatus Status { get; set; } = UnitStatus.Vacant;

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdateUnitRequest
{
    [MaxLength(50)]
    public string? UnitNumber { get; set; }

    [MaxLength(120)]
    public string? FloorPlan { get; set; }

    public decimal? Bedrooms { get; set; }
    public decimal? Bathrooms { get; set; }
    public int? SquareFeet { get; set; }
    public decimal? MarketRent { get; set; }
    public UnitStatus? Status { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
