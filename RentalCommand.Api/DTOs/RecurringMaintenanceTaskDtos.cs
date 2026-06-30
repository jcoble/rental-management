using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="RecurringMaintenanceTask"/>.</summary>
public class RecurringMaintenanceTaskResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? VendorId { get; set; }
    public string? PropertyName { get; set; }
    public string? UnitNumber { get; set; }
    public string? VendorName { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public RecurrenceInterval RecurrenceInterval { get; set; }
    public DateTime NextDueDate { get; set; }
    public TimeOnly? ScheduledTime { get; set; }
    public decimal? EstimatedCost { get; set; }
    public decimal? MonthlyEstimatedCost { get; set; }
    public int GeneratedWorkOrderCount { get; set; }
    public int? LastGeneratedWorkOrderId { get; set; }
    public DateTime? LastGeneratedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public WorkOrderPriority Priority { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>recurring-maintenance-1</c>.</summary>
    public string TestId => $"recurring-maintenance-{Id}";

    public static RecurringMaintenanceTaskResponse FromEntity(RecurringMaintenanceTask e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        UnitId = e.UnitId,
        VendorId = e.VendorId,
        PropertyName = e.Property?.Name,
        UnitNumber = e.Unit?.UnitNumber,
        VendorName = e.Vendor?.Name,
        Title = e.Title,
        Description = e.Description,
        Category = e.Category,
        RecurrenceInterval = e.RecurrenceInterval,
        NextDueDate = e.NextDueDate,
        ScheduledTime = e.ScheduledTime,
        EstimatedCost = e.EstimatedCost,
        MonthlyEstimatedCost = ToMonthlyEstimate(e.EstimatedCost, e.RecurrenceInterval),
        GeneratedWorkOrderCount = e.WorkOrders.Count,
        LastGeneratedWorkOrderId = e.WorkOrders
            .OrderByDescending(w => w.RequestedAt)
            .ThenByDescending(w => w.Id)
            .Select(w => (int?)w.Id)
            .FirstOrDefault(),
        LastGeneratedAtUtc = e.LastGeneratedAtUtc,
        IsActive = e.IsActive,
        Priority = e.Priority,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };

    public static decimal? ToMonthlyEstimate(decimal? estimatedCost, RecurrenceInterval interval)
    {
        if (estimatedCost == null)
            return null;

        return interval switch
        {
            RecurrenceInterval.Weekly => estimatedCost.Value * 52m / 12m,
            RecurrenceInterval.Monthly => estimatedCost.Value,
            RecurrenceInterval.Quarterly => estimatedCost.Value / 3m,
            RecurrenceInterval.SemiAnnually => estimatedCost.Value / 6m,
            RecurrenceInterval.Annually => estimatedCost.Value / 12m,
            _ => estimatedCost.Value,
        };
    }
}

public class RecurringMaintenanceTaskListResponse
{
    public IReadOnlyList<RecurringMaintenanceTaskResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class CreateRecurringMaintenanceTaskRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? VendorId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string? Description { get; set; }

    [MaxLength(120)]
    public string? Category { get; set; }

    public RecurrenceInterval RecurrenceInterval { get; set; } = RecurrenceInterval.Monthly;

    [Required]
    public DateTime NextDueDate { get; set; }

    public TimeOnly? ScheduledTime { get; set; }

    [Range(0, 99999999)]
    public decimal? EstimatedCost { get; set; }

    public bool IsActive { get; set; } = true;

    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;
}

public class UpdateRecurringMaintenanceTaskRequest
{
    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? VendorId { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(4000)]
    public string? Description { get; set; }

    [MaxLength(120)]
    public string? Category { get; set; }

    public RecurrenceInterval? RecurrenceInterval { get; set; }
    public DateTime? NextDueDate { get; set; }
    public TimeOnly? ScheduledTime { get; set; }

    [Range(0, 99999999)]
    public decimal? EstimatedCost { get; set; }

    public bool? IsActive { get; set; }
    public WorkOrderPriority? Priority { get; set; }
}

/// <summary>Flips a recurring task's <see cref="RecurringMaintenanceTask.IsActive"/> switch.</summary>
public class ToggleRecurringMaintenanceTaskActiveRequest
{
    [Required]
    public bool IsActive { get; set; }
}
