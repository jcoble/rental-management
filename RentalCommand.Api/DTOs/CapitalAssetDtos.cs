using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a depreciable capital asset.</summary>
public class CapitalAssetResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public int? UnitId { get; set; }
    public string? UnitNumber { get; set; }
    public int? SourceExpenseId { get; set; }
    public string? SourceExpenseDescription { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal CostBasis { get; set; }
    public DateTime InServiceDate { get; set; }
    public DepreciationMethod Method { get; set; }
    public decimal RecoveryYears { get; set; }
    public DepreciationConvention Convention { get; set; }
    public decimal AccumulatedDepreciation { get; set; }
    public DateTime? DisposedOnDate { get; set; }
    public int DepreciationYear { get; set; }
    public decimal AnnualDepreciation { get; set; }
    public bool IsFirstYearEstimate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string TestId => $"capital-asset-{Id}";

    public static CapitalAssetResponse FromEntity(CapitalAsset e, int depreciationYear)
    {
        var annual = DepreciationCalculator.AnnualForYear(
            e.CostBasis,
            e.InServiceDate,
            e.Method,
            e.RecoveryYears,
            e.Convention,
            e.AccumulatedDepreciation,
            depreciationYear);

        return new CapitalAssetResponse
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            PropertyId = e.PropertyId,
            PropertyName = e.Property?.Name,
            UnitId = e.UnitId,
            UnitNumber = e.Unit?.UnitNumber,
            SourceExpenseId = e.SourceExpenseId,
            SourceExpenseDescription = e.SourceExpense?.Description,
            Description = e.Description,
            CostBasis = e.CostBasis,
            InServiceDate = e.InServiceDate,
            Method = e.Method,
            RecoveryYears = e.RecoveryYears,
            Convention = e.Convention,
            AccumulatedDepreciation = e.AccumulatedDepreciation,
            DisposedOnDate = e.DisposedOnDate,
            DepreciationYear = depreciationYear,
            AnnualDepreciation = annual.Amount,
            IsFirstYearEstimate = annual.IsFirstYearEstimate,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };
    }
}

public class CapitalAssetListResponse
{
    public IReadOnlyList<CapitalAssetResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class CapitalAssetListQuery : ListQuery
{
    [FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "unitId")]
    public int? UnitId { get; set; }

    [FromQuery(Name = "year")]
    public int? Year { get; set; }
}

public class CreateCapitalAssetRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Required]
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [Range(0.01, 999_999_999)]
    public decimal CostBasis { get; set; }

    [Required]
    public DateTime InServiceDate { get; set; }

    [EnumDataType(typeof(DepreciationMethod))]
    public DepreciationMethod Method { get; set; } = DepreciationMethod.StraightLine;

    [Range(1, 40)]
    public decimal RecoveryYears { get; set; } = RecoveryClass.ResidentialBuilding;

    [EnumDataType(typeof(DepreciationConvention))]
    public DepreciationConvention Convention { get; set; } = DepreciationConvention.MidMonth;

    [Range(0, 999_999_999)]
    public decimal AccumulatedDepreciation { get; set; }

    public DateTime? DisposedOnDate { get; set; }
}

public class UpdateCapitalAssetRequest
{
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    public bool? ClearUnit { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [Range(0.01, 999_999_999)]
    public decimal? CostBasis { get; set; }

    public DateTime? InServiceDate { get; set; }

    [EnumDataType(typeof(DepreciationMethod))]
    public DepreciationMethod? Method { get; set; }

    [Range(1, 40)]
    public decimal? RecoveryYears { get; set; }

    [EnumDataType(typeof(DepreciationConvention))]
    public DepreciationConvention? Convention { get; set; }

    [Range(0, 999_999_999)]
    public decimal? AccumulatedDepreciation { get; set; }

    public DateTime? DisposedOnDate { get; set; }
    public bool? ClearDisposedOnDate { get; set; }
}
