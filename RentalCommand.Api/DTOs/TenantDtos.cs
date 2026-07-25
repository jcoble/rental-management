using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;

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

    /// <summary>
    /// Number of Units this tenant currently possesses as a resident party through a canonical
    /// LeaseManagement relationship. Guarantor-only relationships are excluded. Computed DB-side.
    /// </summary>
    public int ActiveLeaseCount { get; set; }

    /// <summary>
    /// Number of distinct live or historical LeaseManagement relationships linked to this tenant.
    /// Agreement corrections and renewals do not inflate this count. Computed DB-side.
    /// </summary>
    public int LeaseHistoryCount { get; set; }

    /// <summary>True only when this tenant has no lease history and can be safely deleted.</summary>
    public bool CanDelete { get; set; } = true;

    /// <summary>User-facing reason delete is disabled, when <see cref="CanDelete"/> is false.</summary>
    public string? DeleteBlockedReason { get; set; }

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

public class TenantListResponse
{
    public IReadOnlyList<TenantResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class TenantListQuery : ListQuery
{
    public bool? AvailableForLease { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    /// <summary>
    /// Keeps parties already attached to this canonical relationship selectable while editing it.
    /// This is a LeaseManagement id, not a legal-agreement version id.
    /// </summary>
    public int? IncludeLeaseManagementId { get; set; }
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

/// <summary>
/// One reviewed Guided Setup action. The complete collection is committed or rolled back as one
/// receipt-backed command; it is deliberately separate from ordinary one-tenant CRUD.
/// </summary>
public sealed class GuidedTenantSetupRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(25)]
    public List<CreateTenantRequest> Tenants { get; set; } = [];
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
