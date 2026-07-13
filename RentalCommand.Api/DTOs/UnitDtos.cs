using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.DTOs;

/// <summary>Presentation-only availability derived from possession and operational periods.</summary>
public enum DerivedUnitStatus
{
    Vacant,
    Reserved,
    Occupied,
    Offline,
}

/// <summary>Wire shape returned for a <see cref="Unit"/>.</summary>
public class UnitResponse
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public string? FloorPlan { get; set; }
    public decimal Bedrooms { get; set; }
    public decimal Bathrooms { get; set; }
    public int? SquareFeet { get; set; }
    public decimal MarketRent { get; set; }
    public DerivedUnitStatus Status { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>unit-1</c>.</summary>
    public string TestId => $"unit-{Id}";

}

/// <summary>
/// A units-list row with cheap health badges for the <c>/units</c> page (spec section 10). Every field
/// is computed DB-side in one projection query (grouped counts + canonical occupancy/lifecycle scalars) — the list
/// never calls the per-unit dashboard per row. <see cref="SimpleStage"/> is a simplified label
/// derived from possession, operational periods, lifecycle, and governing-agreement dates.
/// </summary>
public class UnitHealthResponse
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;

    /// <summary>
    /// Occupancy/availability status derived from possession and operational-period projections,
    /// never the mutable legacy Unit.Status field.
    /// </summary>
    public string Status { get; set; } = string.Empty;
    public decimal MarketRent { get; set; }

    /// <summary>Canonical continuous household/possession relationship currently occupying the Unit.</summary>
    public int? CurrentLeaseManagementId { get; set; }

    /// <summary>Immutable agreement version governing the relationship on the portfolio business date.</summary>
    public int? CurrentAgreementId { get; set; }

    /// <summary>Continuous tenant-account identity for rent, charges, payments, and deposits.</summary>
    public int? TenantAccountId { get; set; }

    /// <summary>Open (not Completed/Cancelled/Archived) work orders on the unit.</summary>
    public int OpenWorkOrderCount { get; set; }

    /// <summary>Days until the governing agreement ends; null for an open-ended/no-current agreement.</summary>
    public int? LeaseEndsInDays { get; set; }

    /// <summary>Documents attached to the unit, canonical legal artifacts, expenses, work orders, or inspections.</summary>
    public int DocsNeedingReviewCount { get; set; }

    /// <summary>Simplified lifecycle label for the list badge (Active/Renewal/Move-Out/Lease/Vacant/Turnover).</summary>
    public string SimpleStage { get; set; } = string.Empty;

    /// <summary>Stable selector for frontend tests, e.g. <c>unit-1</c>.</summary>
    public string TestId => $"unit-{Id}";
}

public class UnitHealthListResponse
{
    public IReadOnlyList<UnitHealthResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class UnitHealthListQuery : ListQuery
{
    public int? PropertyId { get; set; }
    public string? Status { get; set; }
    public string? Stage { get; set; }
}

public class UnitListQuery : ListQuery
{
    public bool? AvailableForLease { get; set; }
}

public class CreateUnitRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Required]
    [MaxLength(50)]
    public string UnitNumber { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? FloorPlan { get; set; }

    [Range(0, 99)]
    public decimal Bedrooms { get; set; }

    [Range(0, 99)]
    public decimal Bathrooms { get; set; }

    [Range(0, 99999)]
    public int? SquareFeet { get; set; }

    [Range(0, 99999999)]
    public decimal MarketRent { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdateUnitRequest
{
    [MaxLength(50)]
    public string? UnitNumber { get; set; }

    [MaxLength(100)]
    public string? FloorPlan { get; set; }

    [Range(0, 99)]
    public decimal? Bedrooms { get; set; }

    [Range(0, 99)]
    public decimal? Bathrooms { get; set; }

    [Range(0, 99999)]
    public int? SquareFeet { get; set; }

    [Range(0, 99999999)]
    public decimal? MarketRent { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
