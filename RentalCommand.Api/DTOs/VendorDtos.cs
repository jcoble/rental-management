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

    /// <summary>Cached average star rating (1–5); null until the vendor has been rated.</summary>
    public decimal? AverageRating { get; set; }

    /// <summary>Number of ratings behind <see cref="AverageRating"/>.</summary>
    public int RatingCount { get; set; }

    /// <summary>Count of work orders this vendor has completed.</summary>
    public int JobsCompleted { get; set; }

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
        AverageRating = e.AverageRating,
        RatingCount = e.RatingCount,
        JobsCompleted = e.JobsCompleted,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class VendorListResponse
{
    public IReadOnlyList<VendorResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

/// <summary>Body for <c>POST /api/v1/vendors/{id}/ratings</c>: a 1–5 star rating of a vendor.</summary>
public class CreateVendorRatingRequest
{
    [Range(1, 5)]
    public int Stars { get; set; }

    [MaxLength(2000)]
    public string? Comment { get; set; }

    /// <summary>Optional work order the rating followed; validated to be in the caller's portfolio.</summary>
    [Range(1, int.MaxValue)]
    public int? WorkOrderId { get; set; }
}

/// <summary>Wire shape returned for a recorded <see cref="VendorRating"/>.</summary>
public class VendorRatingResponse
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public int? WorkOrderId { get; set; }
    public int Stars { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public static VendorRatingResponse FromEntity(VendorRating e) => new()
    {
        Id = e.Id,
        VendorId = e.VendorId,
        WorkOrderId = e.WorkOrderId,
        Stars = e.Stars,
        Comment = e.Comment,
        CreatedAtUtc = e.CreatedAtUtc,
    };
}

/// <summary>Vendor performance scorecard for <c>GET /api/v1/vendors/{id}/scorecard</c>.</summary>
public class VendorScorecardResponse
{
    public int VendorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? AverageRating { get; set; }
    public int RatingCount { get; set; }
    public int JobsCompleted { get; set; }

    /// <summary>
    /// Average hours from when a job was texted to the vendor to their DONE reply, across all
    /// completed dispatches. Null when the vendor has no completed dispatch yet.
    /// </summary>
    public decimal? AvgResponseHours { get; set; }
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
