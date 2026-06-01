using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Lease"/>.</summary>
public class LeaseResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public int TenantId { get; set; }
    public string LeaseNumber { get; set; } = string.Empty;
    public LeaseStatus Status { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime? MoveInDate { get; set; }
    public DateTime? MoveOutDate { get; set; }
    public decimal MonthlyRent { get; set; }
    public decimal SecurityDeposit { get; set; }
    public decimal LateFeeAmount { get; set; }
    public int RentDueDay { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Tenant's full name, projected from the <see cref="Lease.Tenant"/> navigation. Null when not loaded.</summary>
    public string? TenantName { get; set; }

    /// <summary>Unit number, projected from the <see cref="Lease.Unit"/> navigation. Null when not loaded.</summary>
    public string? UnitNumber { get; set; }

    /// <summary>Property name, projected from the <see cref="Lease.Property"/> navigation. Null when not loaded.</summary>
    public string? PropertyName { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>lease-1</c>.</summary>
    public string TestId => $"lease-{Id}";

    public static LeaseResponse FromEntity(Lease e, bool includeNavigations = false)
    {
        var response = new LeaseResponse
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            PropertyId = e.PropertyId,
            UnitId = e.UnitId,
            TenantId = e.TenantId,
            LeaseNumber = e.LeaseNumber,
            Status = e.Status,
            StartDate = e.StartDate,
            EndDate = e.EndDate,
            MoveInDate = e.MoveInDate,
            MoveOutDate = e.MoveOutDate,
            MonthlyRent = e.MonthlyRent,
            SecurityDeposit = e.SecurityDeposit,
            LateFeeAmount = e.LateFeeAmount,
            RentDueDay = e.RentDueDay,
            Notes = e.Notes,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };

        if (includeNavigations)
        {
            // Pickers (scan confirm, payment/deposit create) need human-readable labels,
            // not just the bare lease number. Projected from existing navigations.
            response.TenantName = e.Tenant == null
                ? null
                : $"{e.Tenant.FirstName} {e.Tenant.LastName}".Trim();
            response.UnitNumber = e.Unit?.UnitNumber;
            response.PropertyName = e.Property?.Name;
        }

        return response;
    }
}

public class CreateLeaseRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int UnitId { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int TenantId { get; set; }

    [Required]
    [MaxLength(100)]
    public string LeaseNumber { get; set; } = string.Empty;

    public LeaseStatus Status { get; set; } = LeaseStatus.Draft;

    [Required]
    public DateTime StartDate { get; set; }

    [Required]
    public DateTime EndDate { get; set; }

    public DateTime? MoveInDate { get; set; }
    public DateTime? MoveOutDate { get; set; }

    [Range(0.01, 99999999)]
    public decimal MonthlyRent { get; set; }

    [Range(0, 99999999)]
    public decimal SecurityDeposit { get; set; }

    [Range(0, 99999999)]
    public decimal LateFeeAmount { get; set; }

    [Range(1, 31)]
    public int RentDueDay { get; set; } = 1;

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdateLeaseRequest
{
    [MaxLength(100)]
    public string? LeaseNumber { get; set; }

    public LeaseStatus? Status { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? MoveInDate { get; set; }
    public DateTime? MoveOutDate { get; set; }

    [Range(0.01, 99999999)]
    public decimal? MonthlyRent { get; set; }

    [Range(0, 99999999)]
    public decimal? SecurityDeposit { get; set; }

    [Range(0, 99999999)]
    public decimal? LateFeeAmount { get; set; }

    [Range(1, 31)]
    public int? RentDueDay { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
