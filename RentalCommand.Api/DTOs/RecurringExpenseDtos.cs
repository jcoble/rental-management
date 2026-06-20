using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="RecurringExpense"/> template.</summary>
public class RecurringExpenseResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public int? UnitId { get; set; }
    public ScheduleECategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public RecurringExpenseFrequency Frequency { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime NextRunDate { get; set; }
    public bool Active { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string TestId => $"recurring-expense-{Id}";

    public static RecurringExpenseResponse FromEntity(RecurringExpense e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        PropertyName = e.Property?.Name,
        UnitId = e.UnitId,
        Category = e.Category,
        Description = e.Description,
        Amount = e.Amount,
        Frequency = e.Frequency,
        StartDate = e.StartDate,
        NextRunDate = e.NextRunDate,
        Active = e.Active,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class CreateRecurringExpenseRequest
{
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    public ScheduleECategory Category { get; set; } = ScheduleECategory.Other;

    [Required]
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [Range(0.01, 99_999_999)]
    public decimal Amount { get; set; }

    public RecurringExpenseFrequency Frequency { get; set; } = RecurringExpenseFrequency.Monthly;

    [Required]
    public DateTime StartDate { get; set; }

    /// <summary>First run date; defaults to <see cref="StartDate"/> when omitted.</summary>
    public DateTime? NextRunDate { get; set; }

    public bool Active { get; set; } = true;

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdateRecurringExpenseRequest
{
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    public ScheduleECategory? Category { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [Range(0.01, 99_999_999)]
    public decimal? Amount { get; set; }

    public RecurringExpenseFrequency? Frequency { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? NextRunDate { get; set; }
    public bool? Active { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
