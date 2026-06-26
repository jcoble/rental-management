using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

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
    public UnitStatus Status { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>unit-1</c>.</summary>
    public string TestId => $"unit-{Id}";

    public static UnitResponse FromEntity(Unit e) => new()
    {
        Id = e.Id,
        PropertyId = e.PropertyId,
        UnitNumber = e.UnitNumber,
        FloorPlan = e.FloorPlan,
        Bedrooms = e.Bedrooms,
        Bathrooms = e.Bathrooms,
        SquareFeet = e.SquareFeet,
        MarketRent = e.MarketRent,
        Status = e.Status,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

/// <summary>
/// A units-list row with cheap health badges for the <c>/units</c> page (spec section 10). Every field
/// is computed DB-side in one projection query (grouped counts + the current-lease scalars) — the list
/// never calls the per-unit dashboard per row. <see cref="SimpleStage"/> is a simplified label
/// (Unit.Status + current-lease status), NOT the full 9-stage detail-page derivation.
/// </summary>
public class UnitHealthResponse
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;

    /// <summary>Occupancy status as its string name (e.g. <c>Occupied</c>).</summary>
    public string Status { get; set; } = string.Empty;
    public decimal MarketRent { get; set; }

    /// <summary>Open (not Completed/Cancelled/Archived) work orders on the unit.</summary>
    public int OpenWorkOrderCount { get; set; }

    /// <summary>Days until the unit's active lease ends; null when there is no active lease.</summary>
    public int? LeaseEndsInDays { get; set; }

    /// <summary>Documents attached to the unit or its lease/payment/expense/work-order/inspection children.</summary>
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

    public UnitStatus Status { get; set; } = UnitStatus.Vacant;

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

    public UnitStatus? Status { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
