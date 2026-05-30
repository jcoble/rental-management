using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Vendor"/>.</summary>
public class VendorResponse
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

    /// <summary>Stable selector for frontend tests, e.g. <c>vendor-1</c>.</summary>
    public string TestId => $"vendor-{Id}";

    public static VendorResponse FromEntity(Vendor e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        Name = e.Name,
        ServiceType = e.ServiceType,
        Email = e.Email,
        Phone = e.Phone,
        TaxId = e.TaxId,
        Is1099Eligible = e.Is1099Eligible,
        W9OnFile = e.W9OnFile,
        Preferred = e.Preferred,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class CreateVendorRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(120)]
    public string ServiceType { get; set; } = string.Empty;

    [MaxLength(200)]
    [EmailAddress]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(50)]
    public string? TaxId { get; set; }

    public bool Is1099Eligible { get; set; }
    public bool W9OnFile { get; set; }
    public bool Preferred { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdateVendorRequest
{
    [MaxLength(200)]
    public string? Name { get; set; }

    [MaxLength(120)]
    public string? ServiceType { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(50)]
    public string? TaxId { get; set; }

    public bool? Is1099Eligible { get; set; }
    public bool? W9OnFile { get; set; }
    public bool? Preferred { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
