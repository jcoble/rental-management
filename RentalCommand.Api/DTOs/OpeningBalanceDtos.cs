using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for an <see cref="OpeningBalance"/> (a lease's carried-over balance).</summary>
public class OpeningBalanceResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }

    /// <summary>Signed carried-over amount: positive = owed by the tenant, negative = a credit.</summary>
    public decimal Amount { get; set; }

    /// <summary>The "books start" date this balance was true as of.</summary>
    public DateTime AsOfDate { get; set; }

    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>opening-balance-1</c>.</summary>
    public string TestId => $"opening-balance-{Id}";

    public static OpeningBalanceResponse FromEntity(OpeningBalance e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        LeaseId = e.LeaseId,
        Amount = e.Amount,
        AsOfDate = e.AsOfDate,
        Note = e.Note,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class CreateOpeningBalanceRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int LeaseId { get; set; }

    /// <summary>Signed: positive = tenant owed this when you started, negative = a credit.</summary>
    [Range(-99999999, 99999999)]
    public decimal Amount { get; set; }

    [Required]
    public DateTime AsOfDate { get; set; }

    [MaxLength(2000)]
    public string? Note { get; set; }
}

public class UpdateOpeningBalanceRequest
{
    [Range(-99999999, 99999999)]
    public decimal? Amount { get; set; }

    public DateTime? AsOfDate { get; set; }

    [MaxLength(2000)]
    public string? Note { get; set; }
}
