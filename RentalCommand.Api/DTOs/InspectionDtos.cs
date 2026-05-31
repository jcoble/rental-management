using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for an <see cref="Inspection"/>.</summary>
public class InspectionResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? LeaseId { get; set; }
    public InspectionType Type { get; set; }
    public InspectionStatus Status { get; set; }
    public DateTime ScheduledFor { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Outcome { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>inspection-1</c>.</summary>
    public string TestId => $"inspection-{Id}";

    public static InspectionResponse FromEntity(Inspection e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        UnitId = e.UnitId,
        LeaseId = e.LeaseId,
        Type = e.Type,
        Status = e.Status,
        ScheduledFor = e.ScheduledFor,
        CompletedAt = e.CompletedAt,
        Outcome = e.Outcome,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class CreateInspectionRequest
{
    [Required]
    public int PropertyId { get; set; }

    public int? UnitId { get; set; }
    public int? LeaseId { get; set; }

    public InspectionType Type { get; set; } = InspectionType.Routine;
    public InspectionStatus Status { get; set; } = InspectionStatus.Scheduled;

    [Required]
    public DateTime ScheduledFor { get; set; }

    public DateTime? CompletedAt { get; set; }

    [MaxLength(500)]
    public string? Outcome { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

public class UpdateInspectionRequest
{
    public int? UnitId { get; set; }
    public int? LeaseId { get; set; }

    public InspectionType? Type { get; set; }
    public InspectionStatus? Status { get; set; }

    public DateTime? ScheduledFor { get; set; }
    public DateTime? CompletedAt { get; set; }

    [MaxLength(500)]
    public string? Outcome { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
