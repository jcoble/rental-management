using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Tenant"/>.</summary>
public class TenantResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? EmergencyContact { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Notes { get; set; }

    /// <summary>Number of this tenant's leases currently in <see cref="LeaseStatus.Active"/>. Computed DB-side.</summary>
    public int ActiveLeaseCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>tenant-1</c>.</summary>
    public string TestId => $"tenant-{Id}";

    public static TenantResponse FromEntity(Tenant e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        FirstName = e.FirstName,
        LastName = e.LastName,
        Email = e.Email,
        Phone = e.Phone,
        EmergencyContact = e.EmergencyContact,
        DateOfBirth = e.DateOfBirth,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class CreateTenantRequest
{
    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(200)]
    [EmailAddress]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    public string? EmergencyContact { get; set; }

    public DateTime? DateOfBirth { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdateTenantRequest
{
    [MaxLength(100)]
    public string? FirstName { get; set; }

    [MaxLength(100)]
    public string? LastName { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    public string? EmergencyContact { get; set; }

    public DateTime? DateOfBirth { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
