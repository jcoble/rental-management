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
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public RecurrenceInterval RecurrenceInterval { get; set; }
    public DateTime NextDueDate { get; set; }
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
        Title = e.Title,
        Description = e.Description,
        Category = e.Category,
        RecurrenceInterval = e.RecurrenceInterval,
        NextDueDate = e.NextDueDate,
        LastGeneratedAtUtc = e.LastGeneratedAtUtc,
        IsActive = e.IsActive,
        Priority = e.Priority,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
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
    public bool? IsActive { get; set; }
    public WorkOrderPriority? Priority { get; set; }
}

/// <summary>Flips a recurring task's <see cref="RecurringMaintenanceTask.IsActive"/> switch.</summary>
public class ToggleRecurringMaintenanceTaskActiveRequest
{
    [Required]
    public bool IsActive { get; set; }
}
