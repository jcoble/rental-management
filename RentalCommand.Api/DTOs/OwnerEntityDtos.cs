using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for an <see cref="OwnerEntity"/> (Person/LLC/Trust).</summary>
public class OwnerEntityResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public OwnerEntityType OwnerEntityType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TaxId { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>owner-entity-1</c>.</summary>
    public string TestId => $"owner-entity-{Id}";

    public static OwnerEntityResponse FromEntity(OwnerEntity e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        OwnerEntityType = e.OwnerEntityType,
        Name = e.Name,
        TaxId = e.TaxId,
        Address = e.Address,
        Phone = e.Phone,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class CreateOwnerEntityRequest
{
    public OwnerEntityType OwnerEntityType { get; set; } = OwnerEntityType.Person;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? TaxId { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }
}

public class UpdateOwnerEntityRequest
{
    public OwnerEntityType? OwnerEntityType { get; set; }

    [MaxLength(200)]
    public string? Name { get; set; }

    [MaxLength(50)]
    public string? TaxId { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }
}
