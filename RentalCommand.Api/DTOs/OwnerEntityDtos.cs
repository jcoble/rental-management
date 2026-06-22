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
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    /// <summary>True for the self-owner auto-created from the landlord's own account at onboarding.</summary>
    public bool IsPrimary { get; set; }

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
        AddressLine1 = e.AddressLine1,
        AddressLine2 = e.AddressLine2,
        City = e.City,
        State = e.State,
        PostalCode = e.PostalCode,
        Address = e.Address,
        Phone = e.Phone,
        Email = e.Email,
        IsPrimary = e.IsPrimary,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class OwnerEntityListResponse
{
    public IReadOnlyList<OwnerEntityResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class CreateOwnerEntityRequest
{
    public OwnerEntityType OwnerEntityType { get; set; } = OwnerEntityType.Person;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? TaxId { get; set; }

    [MaxLength(250)]
    public string? AddressLine1 { get; set; }

    [MaxLength(250)]
    public string? AddressLine2 { get; set; }

    [MaxLength(120)]
    public string? City { get; set; }

    [MaxLength(60)]
    public string? State { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(254)]
    [EmailAddress]
    public string? Email { get; set; }
}

public class UpdateOwnerEntityRequest
{
    public OwnerEntityType? OwnerEntityType { get; set; }

    [MaxLength(200)]
    public string? Name { get; set; }

    [MaxLength(50)]
    public string? TaxId { get; set; }

    [MaxLength(250)]
    public string? AddressLine1 { get; set; }

    [MaxLength(250)]
    public string? AddressLine2 { get; set; }

    [MaxLength(120)]
    public string? City { get; set; }

    [MaxLength(60)]
    public string? State { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(254)]
    [EmailAddress]
    public string? Email { get; set; }
}
