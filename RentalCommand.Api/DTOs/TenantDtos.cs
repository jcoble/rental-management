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

    /// <summary>
    /// Portal-login state for this tenant: <c>"none"</c> (no Identity login), <c>"active"</c> (login,
    /// can sign in), or <c>"disabled"</c> (login locked off). Only populated on the single-tenant GET
    /// (it requires an Identity join); null on list/create/update responses.
    /// </summary>
    public string? PortalAccess { get; set; }

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

/// <summary>Request body for the staff portal-access toggle: turn the tenant's login on or off.</summary>
public class SetPortalAccessRequest
{
    /// <summary>True to enable the tenant's portal login (provisioning one if needed), false to turn it off.</summary>
    public bool Enabled { get; set; }
}

/// <summary>Result of the staff portal-access toggle for a tenant.</summary>
public class PortalAccessResponse
{
    /// <summary>The resulting portal-login state: <c>"none"</c>, <c>"active"</c>, or <c>"disabled"</c>.</summary>
    public string PortalAccess { get; set; } = "none";

    /// <summary>The email the tenant signs in with, when known.</summary>
    public string? Email { get; set; }
}

/// <summary>Result of the staff "send / resend portal invite" action for a tenant.</summary>
public class PortalInviteResponse
{
    /// <summary>The email the invite was sent to (the tenant's sign-in email).</summary>
    public string? Email { get; set; }

    /// <summary>True when the tenant already had a portal login (this was a resend).</summary>
    public bool AlreadyExisted { get; set; }
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
