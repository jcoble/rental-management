using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Portfolio"/>. Never expose the entity directly.</summary>
public class PortfolioResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ManagementCompanyName { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public PortfolioStatus Status { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Settings { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>portfolio-1</c>.</summary>
    public string TestId => $"portfolio-{Id}";

    public static PortfolioResponse FromEntity(Portfolio e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Description = e.Description,
        ManagementCompanyName = e.ManagementCompanyName,
        TimeZone = e.TimeZone,
        Status = e.Status,
        Currency = e.Currency,
        Settings = e.Settings,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class CreatePortfolioRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string ManagementCompanyName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? TimeZone { get; set; }

    [MaxLength(8)]
    public string? Currency { get; set; }

    public string? Settings { get; set; }
}

public class UpdatePortfolioRequest
{
    [MaxLength(200)]
    public string? Name { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? ManagementCompanyName { get; set; }

    [MaxLength(100)]
    public string? TimeZone { get; set; }

    public PortfolioStatus? Status { get; set; }

    [MaxLength(8)]
    public string? Currency { get; set; }

    public string? Settings { get; set; }
}
