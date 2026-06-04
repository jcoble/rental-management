using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for an <see cref="Inspection"/> (list + summary contexts).</summary>
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
    public int? TemplateId { get; set; }
    public int? ReportStoredFileId { get; set; }
    public string? Inspector { get; set; }
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
        TemplateId = e.TemplateId,
        ReportStoredFileId = e.ReportStoredFileId,
        Inspector = e.Inspector,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

/// <summary>One checklist item on an inspection.</summary>
public class InspectionItemResponse
{
    public int Id { get; set; }
    public int InspectionId { get; set; }
    public string Area { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public InspectionItemResult Result { get; set; }
    public string? Note { get; set; }
    public int? PhotoStoredFileId { get; set; }
    public int? SpawnedWorkOrderId { get; set; }
    public int SortOrder { get; set; }

    public static InspectionItemResponse FromEntity(InspectionItem e) => new()
    {
        Id = e.Id,
        InspectionId = e.InspectionId,
        Area = e.Area,
        Label = e.Label,
        Result = e.Result,
        Note = e.Note,
        PhotoStoredFileId = e.PhotoStoredFileId,
        SpawnedWorkOrderId = e.SpawnedWorkOrderId,
        SortOrder = e.SortOrder,
    };
}

/// <summary>Inspection detail: the base inspection plus its checklist <see cref="Items"/> (ascending order).</summary>
public class InspectionDetailResponse : InspectionResponse
{
    public IReadOnlyList<InspectionItemResponse> Items { get; set; } = [];

    public static InspectionDetailResponse FromEntity(Inspection e, IEnumerable<InspectionItem> items)
    {
        var detail = new InspectionDetailResponse
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
            TemplateId = e.TemplateId,
            ReportStoredFileId = e.ReportStoredFileId,
            Inspector = e.Inspector,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
            Items = items
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.Id)
                .Select(InspectionItemResponse.FromEntity)
                .ToList(),
        };
        return detail;
    }
}

/// <summary>A template item in a <see cref="InspectionTemplateResponse"/>.</summary>
public class InspectionTemplateItemResponse
{
    public string Area { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    public static InspectionTemplateItemResponse FromEntity(InspectionTemplateItem e) => new()
    {
        Area = e.Area,
        Label = e.Label,
        SortOrder = e.SortOrder,
    };
}

/// <summary>An available inspection template (built-in or custom) with its items.</summary>
public class InspectionTemplateResponse
{
    public int Id { get; set; }
    public int? PortfolioId { get; set; }
    public string Name { get; set; } = string.Empty;
    public InspectionType InspectionType { get; set; }
    public bool IsBuiltIn { get; set; }
    public IReadOnlyList<InspectionTemplateItemResponse> Items { get; set; } = [];

    public static InspectionTemplateResponse FromEntity(InspectionTemplate e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        Name = e.Name,
        InspectionType = e.InspectionType,
        IsBuiltIn = e.IsBuiltIn,
        Items = e.Items
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Id)
            .Select(InspectionTemplateItemResponse.FromEntity)
            .ToList(),
    };
}

/// <summary>Body for PATCH /inspections/{id}/items/{itemId} — set the result and/or note.</summary>
public class UpdateInspectionItemRequest
{
    public InspectionItemResult? Result { get; set; }

    [MaxLength(2000)]
    public string? Note { get; set; }
}

/// <summary>Body for POST /inspections/{id}/items/{itemId}/photo — link an uploaded StoredFile.</summary>
public class AttachInspectionItemPhotoRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int StoredFileId { get; set; }
}

/// <summary>Result of completing an inspection: status, generated report, and spawned work orders.</summary>
public class CompleteInspectionResponse
{
    public int InspectionId { get; set; }
    public InspectionStatus Status { get; set; }
    public int TotalItems { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public int NotApplicableCount { get; set; }
    public int PendingCount { get; set; }

    /// <summary>The generated PDF report file id; download via GET /inspections/{id}/report.</summary>
    public int? ReportStoredFileId { get; set; }

    /// <summary>Ids of the work orders auto-created, one per Fail item.</summary>
    public IReadOnlyList<int> CreatedWorkOrderIds { get; set; } = [];
}

public class CreateInspectionRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
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

    [MaxLength(200)]
    public string? Inspector { get; set; }

    /// <summary>
    /// Optional template to start the inspection from. A built-in template (negative id from
    /// <c>GET /inspections/templates</c>) or a custom portfolio template materializes the checklist
    /// items (all Pending). When omitted, the inspection starts with no items.
    /// </summary>
    public int? TemplateId { get; set; }
}

public class UpdateInspectionRequest
{
    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? LeaseId { get; set; }

    public InspectionType? Type { get; set; }
    public InspectionStatus? Status { get; set; }

    public DateTime? ScheduledFor { get; set; }
    public DateTime? CompletedAt { get; set; }

    [MaxLength(500)]
    public string? Outcome { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    [MaxLength(200)]
    public string? Inspector { get; set; }
}
