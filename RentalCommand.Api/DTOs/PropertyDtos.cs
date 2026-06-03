using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Property"/>.</summary>
public class PropertyResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? OwnerId { get; set; }
    public int? OwnerEntityId { get; set; }
    public string? OwnerName { get; set; }
    public string Name { get; set; } = string.Empty;
    public PropertyType PropertyType { get; set; }
    public PropertyStatus Status { get; set; }
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

    /// <summary>Stable selector for frontend tests, e.g. <c>property-1</c>.</summary>
    public string TestId => $"property-{Id}";

    public static PropertyResponse FromEntity(Property e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        OwnerId = e.OwnerId,
        OwnerEntityId = e.OwnerEntityId,
        OwnerName = e.OwnerEntity?.Name ?? e.Owner?.Name,
        Name = e.Name,
        PropertyType = e.PropertyType,
        Status = e.Status,
        AddressLine1 = e.AddressLine1,
        AddressLine2 = e.AddressLine2,
        City = e.City,
        State = e.State,
        PostalCode = e.PostalCode,
        YearBuilt = e.YearBuilt,
        ManagementFeePercent = e.ManagementFeePercent,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class CreatePropertyRequest
{
    [Range(1, int.MaxValue)]
    public int? OwnerId { get; set; }

    [Range(1, int.MaxValue)]
    public int? OwnerEntityId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public PropertyType PropertyType { get; set; } = PropertyType.MultiFamily;
    public PropertyStatus Status { get; set; } = PropertyStatus.Active;

    [Required]
    [MaxLength(250)]
    public string AddressLine1 { get; set; } = string.Empty;

    [MaxLength(250)]
    public string? AddressLine2 { get; set; }

    [Required]
    [MaxLength(100)]
    public string City { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string State { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string PostalCode { get; set; } = string.Empty;

    [Range(1800, 2200)]
    public int? YearBuilt { get; set; }

    [Range(0, 100)]
    public decimal? ManagementFeePercent { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdatePropertyRequest
{
    [Range(1, int.MaxValue)]
    public int? OwnerId { get; set; }

    [Range(1, int.MaxValue)]
    public int? OwnerEntityId { get; set; }

    [MaxLength(200)]
    public string? Name { get; set; }

    public PropertyType? PropertyType { get; set; }
    public PropertyStatus? Status { get; set; }

    [MaxLength(250)]
    public string? AddressLine1 { get; set; }

    [MaxLength(250)]
    public string? AddressLine2 { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(100)]
    public string? State { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }

    [Range(1800, 2200)]
    public int? YearBuilt { get; set; }

    [Range(0, 100)]
    public decimal? ManagementFeePercent { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
