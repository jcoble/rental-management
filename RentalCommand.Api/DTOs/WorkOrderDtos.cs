using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="WorkOrder"/>.</summary>
public class WorkOrderResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? TenantId { get; set; }
    public int? LeaseId { get; set; }
    public int? VendorId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public WorkOrderPriority Priority { get; set; }
    public WorkOrderStatus Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ScheduledFor { get; set; }
    public DateTime? CompletedAt { get; set; }
    public decimal? EstimatedCost { get; set; }
    public decimal? ActualCost { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>work-order-1</c>.</summary>
    public string TestId => $"work-order-{Id}";

    public static WorkOrderResponse FromEntity(WorkOrder e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        UnitId = e.UnitId,
        TenantId = e.TenantId,
        LeaseId = e.LeaseId,
        VendorId = e.VendorId,
        Title = e.Title,
        Description = e.Description,
        Category = e.Category,
        Priority = e.Priority,
        Status = e.Status,
        RequestedAt = e.RequestedAt,
        ScheduledFor = e.ScheduledFor,
        CompletedAt = e.CompletedAt,
        EstimatedCost = e.EstimatedCost,
        ActualCost = e.ActualCost,
        CreatedBy = e.CreatedBy,
        UpdatedAt = e.UpdatedAt,
    };
}

/// <summary>One entry in a work order's status timeline (oldest → newest in the parent list).</summary>
public class WorkOrderStatusEventResponse
{
    public int Id { get; set; }

    /// <summary>Status moved away from; null for the initial create event.</summary>
    public WorkOrderStatus? FromStatus { get; set; }

    /// <summary>Status moved into.</summary>
    public WorkOrderStatus ToStatus { get; set; }

    public string? Note { get; set; }

    /// <summary>Human label for who/what made the change, e.g. "Staff", "Tenant", "System".</summary>
    public string? ChangedByLabel { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public static WorkOrderStatusEventResponse FromEntity(WorkOrderStatusEvent e) => new()
    {
        Id = e.Id,
        FromStatus = e.FromStatus,
        ToStatus = e.ToStatus,
        Note = e.Note,
        ChangedByLabel = e.ChangedByLabel,
        CreatedAtUtc = e.CreatedAtUtc,
    };
}

/// <summary>
/// Work-order detail: the full <see cref="WorkOrderResponse"/> plus the status <see cref="Timeline"/>
/// (oldest → newest). Returned only on the single-work-order GET; the list endpoint stays lightweight.
/// </summary>
public class WorkOrderDetailResponse : WorkOrderResponse
{
    public IReadOnlyList<WorkOrderStatusEventResponse> Timeline { get; set; } = [];

    public static WorkOrderDetailResponse FromEntity(WorkOrder e, IEnumerable<WorkOrderStatusEvent> events)
    {
        var detail = new WorkOrderDetailResponse
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            PropertyId = e.PropertyId,
            UnitId = e.UnitId,
            TenantId = e.TenantId,
            LeaseId = e.LeaseId,
            VendorId = e.VendorId,
            Title = e.Title,
            Description = e.Description,
            Category = e.Category,
            Priority = e.Priority,
            Status = e.Status,
            RequestedAt = e.RequestedAt,
            ScheduledFor = e.ScheduledFor,
            CompletedAt = e.CompletedAt,
            EstimatedCost = e.EstimatedCost,
            ActualCost = e.ActualCost,
            CreatedBy = e.CreatedBy,
            UpdatedAt = e.UpdatedAt,
            Timeline = events
                .OrderBy(ev => ev.CreatedAtUtc)
                .ThenBy(ev => ev.Id)
                .Select(WorkOrderStatusEventResponse.FromEntity)
                .ToList(),
        };
        return detail;
    }
}

public class CreateWorkOrderRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? TenantId { get; set; }

    [Range(1, int.MaxValue)]
    public int? LeaseId { get; set; }

    [Range(1, int.MaxValue)]
    public int? VendorId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Category { get; set; } = "General";

    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.New;

    public DateTime? RequestedAt { get; set; }
    public DateTime? ScheduledFor { get; set; }
    public DateTime? CompletedAt { get; set; }

    [Range(0, 99999999)]
    public decimal? EstimatedCost { get; set; }

    [Range(0, 99999999)]
    public decimal? ActualCost { get; set; }

    [MaxLength(120)]
    public string? CreatedBy { get; set; }
}

public class UpdateWorkOrderRequest
{
    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? TenantId { get; set; }

    [Range(1, int.MaxValue)]
    public int? LeaseId { get; set; }

    [Range(1, int.MaxValue)]
    public int? VendorId { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(4000)]
    public string? Description { get; set; }

    [MaxLength(120)]
    public string? Category { get; set; }

    public WorkOrderPriority? Priority { get; set; }
    public WorkOrderStatus? Status { get; set; }

    /// <summary>
    /// Optional free-text note recorded on the status timeline when this update changes
    /// <see cref="Status"/> (e.g. "parts ordered", "tenant let us in"). Ignored when the status
    /// is unchanged.
    /// </summary>
    [MaxLength(2000)]
    public string? StatusNote { get; set; }

    public DateTime? ScheduledFor { get; set; }
    public DateTime? CompletedAt { get; set; }

    [Range(0, 99999999)]
    public decimal? EstimatedCost { get; set; }

    [Range(0, 99999999)]
    public decimal? ActualCost { get; set; }
}
