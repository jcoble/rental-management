using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Services;

namespace RentalCommand.Api.DTOs;

public class PropertyDispositionResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public DateTime ClosedOnDate { get; set; }
    public decimal SalePrice { get; set; }
    public decimal SellingCosts { get; set; }
    public decimal NetSaleProceeds { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal LandValue { get; set; }
    public decimal BuildingBasis { get; set; }
    public decimal AccumulatedDepreciationBeforeSale { get; set; }
    public decimal SaleYearDepreciation { get; set; }
    public decimal TotalDepreciation { get; set; }
    public decimal AdjustedBasis { get; set; }
    public decimal GainLoss { get; set; }
    public decimal UnrecapturedSection1250Gain { get; set; }
    public string? BuyerName { get; set; }
    public string? Memo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string TestId => $"property-disposition-{Id}";

    public static PropertyDispositionResponse FromEntity(PropertyDisposition e)
    {
        var property = e.Property;
        var tax = PropertyDispositionCalculator.Calculate(
            new PropertyDepreciationBasis(
                property?.PurchasePrice,
                property?.LandValue,
                property?.InServiceDate,
                property?.ManualAnnualDepreciation,
                property?.AccumulatedDepreciation ?? 0m),
            e.ClosedOnDate,
            e.SalePrice,
            e.SellingCosts);

        return new PropertyDispositionResponse
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            PropertyId = e.PropertyId,
            PropertyName = property?.Name,
            ClosedOnDate = e.ClosedOnDate,
            SalePrice = e.SalePrice,
            SellingCosts = e.SellingCosts,
            NetSaleProceeds = tax.NetSaleProceeds,
            PurchasePrice = tax.PurchasePrice,
            LandValue = tax.LandValue,
            BuildingBasis = tax.BuildingBasis,
            AccumulatedDepreciationBeforeSale = tax.AccumulatedDepreciationBeforeSale,
            SaleYearDepreciation = tax.SaleYearDepreciation,
            TotalDepreciation = tax.TotalDepreciation,
            AdjustedBasis = tax.AdjustedBasis,
            GainLoss = tax.GainLoss,
            UnrecapturedSection1250Gain = tax.UnrecapturedSection1250Gain,
            BuyerName = e.BuyerName,
            Memo = e.Memo,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };
    }
}

public class PropertyDispositionListResponse
{
    public IReadOnlyList<PropertyDispositionResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class PropertyDispositionListQuery : ListQuery
{
    [FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "year")]
    public int? Year { get; set; }
}

public class CreatePropertyDispositionRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Required]
    public DateTime ClosedOnDate { get; set; }

    [Range(0.01, 999_999_999)]
    public decimal SalePrice { get; set; }

    [Range(0, 999_999_999)]
    public decimal SellingCosts { get; set; }

    [MaxLength(200)]
    public string? BuyerName { get; set; }

    [MaxLength(1000)]
    public string? Memo { get; set; }
}

public class UpdatePropertyDispositionRequest
{
    public DateTime? ClosedOnDate { get; set; }

    [Range(0.01, 999_999_999)]
    public decimal? SalePrice { get; set; }

    [Range(0, 999_999_999)]
    public decimal? SellingCosts { get; set; }

    [MaxLength(200)]
    public string? BuyerName { get; set; }

    [MaxLength(1000)]
    public string? Memo { get; set; }
}
