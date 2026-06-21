using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for an <see cref="Appointment"/>.</summary>
public class AppointmentResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? LeaseId { get; set; }
    public int? TenantId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? ProspectName { get; set; }
    public string? ProspectEmail { get; set; }
    public AppointmentType Type { get; set; }
    public AppointmentStatus Status { get; set; }
    public DateTime ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public string? AssignedTo { get; set; }
    public string? Notes { get; set; }

    /// <summary>Name of the linked property, when assigned. Projected from the navigation.</summary>
    public string? PropertyName { get; set; }

    /// <summary>Unit number of the linked unit, when assigned. Projected from the navigation.</summary>
    public string? UnitNumber { get; set; }

    /// <summary>Full name of the linked tenant, when assigned. Projected from the navigation.</summary>
    public string? TenantName { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>appointment-1</c>.</summary>
    public string TestId => $"appointment-{Id}";

    public static AppointmentResponse FromEntity(Appointment e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        UnitId = e.UnitId,
        LeaseId = e.LeaseId,
        TenantId = e.TenantId,
        Title = e.Title,
        ProspectName = e.ProspectName,
        ProspectEmail = e.ProspectEmail,
        Type = e.Type,
        Status = e.Status,
        ScheduledStart = e.ScheduledStart,
        ScheduledEnd = e.ScheduledEnd,
        AssignedTo = e.AssignedTo,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        // Populated only when the caller eager-loads the Property/Unit/Tenant navigations.
        PropertyName = e.Property?.Name,
        UnitNumber = e.Unit?.UnitNumber,
        TenantName = e.Tenant == null ? null : $"{e.Tenant.FirstName} {e.Tenant.LastName}".Trim(),
    };
}

public class CreateAppointmentRequest
{
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? LeaseId { get; set; }

    [Range(1, int.MaxValue)]
    public int? TenantId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ProspectName { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public string? ProspectEmail { get; set; }

    [EnumDataType(typeof(AppointmentType))]
    public AppointmentType Type { get; set; } = AppointmentType.Showing;

    [EnumDataType(typeof(AppointmentStatus))]
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;

    [Required]
    public DateTime ScheduledStart { get; set; }

    public DateTime? ScheduledEnd { get; set; }

    [MaxLength(120)]
    public string? AssignedTo { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdateAppointmentRequest
{
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? LeaseId { get; set; }

    [Range(1, int.MaxValue)]
    public int? TenantId { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(200)]
    public string? ProspectName { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public string? ProspectEmail { get; set; }

    [EnumDataType(typeof(AppointmentType))]
    public AppointmentType? Type { get; set; }

    [EnumDataType(typeof(AppointmentStatus))]
    public AppointmentStatus? Status { get; set; }

    public DateTime? ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }

    [MaxLength(120)]
    public string? AssignedTo { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
