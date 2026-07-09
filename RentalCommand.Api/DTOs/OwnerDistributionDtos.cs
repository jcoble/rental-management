using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for an <see cref="OwnerDistribution"/> cash payout.</summary>
public class OwnerDistributionResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int OwnerEntityId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public int? PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public DistributionMethod Method { get; set; }
    public string? Memo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string TestId => $"owner-distribution-{Id}";
}

public class OwnerDistributionListResponse
{
    public IReadOnlyList<OwnerDistributionResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class OwnerDistributionListQuery : ListQuery
{
    [FromQuery(Name = "ownerEntityId")]
    public int? OwnerEntityId { get; set; }

    [FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "year")]
    public int? Year { get; set; }
}

public class CreateOwnerDistributionRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int OwnerEntityId { get; set; }

    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [Required]
    public DateTime Date { get; set; }

    [Range(0.01, 999_999_999)]
    public decimal Amount { get; set; }

    public DistributionMethod Method { get; set; } = DistributionMethod.Check;

    [MaxLength(500)]
    public string? Memo { get; set; }
}

public class UpdateOwnerDistributionRequest
{
    [Range(1, int.MaxValue)]
    public int? OwnerEntityId { get; set; }

    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    public bool? ClearProperty { get; set; }

    public DateTime? Date { get; set; }

    [Range(0.01, 999_999_999)]
    public decimal? Amount { get; set; }

    public DistributionMethod? Method { get; set; }

    [MaxLength(500)]
    public string? Memo { get; set; }
}
