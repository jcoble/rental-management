using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public class EvictionCaseResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public string? LeaseNumber { get; set; }
    public int PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public int UnitId { get; set; }
    public string? UnitNumber { get; set; }
    public int TenantId { get; set; }
    public string? TenantName { get; set; }
    public EvictionCaseStatus Status { get; set; }
    public DateTime? FiledOnDate { get; set; }
    public DateTime? HearingDate { get; set; }
    public DateTime? ResolvedOnDate { get; set; }
    public string? CourtName { get; set; }
    public string? CaseNumber { get; set; }
    public string? Resolution { get; set; }
    public string? Notes { get; set; }
    public int EventCount { get; set; }
    public DateTime? LatestEventDate { get; set; }
    public IReadOnlyList<EvictionCaseEventResponse> Events { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string TestId => $"eviction-case-{Id}";

    public static EvictionCaseResponse FromEntity(EvictionCase e, bool includeEvents = false)
    {
        return new EvictionCaseResponse
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            LeaseId = e.LeaseId,
            LeaseNumber = e.Lease?.LeaseNumber,
            PropertyId = e.PropertyId,
            PropertyName = e.Property?.Name,
            UnitId = e.UnitId,
            UnitNumber = e.Unit?.UnitNumber,
            TenantId = e.TenantId,
            TenantName = e.Tenant == null ? null : $"{e.Tenant.FirstName} {e.Tenant.LastName}".Trim(),
            Status = e.Status,
            FiledOnDate = e.FiledOnDate,
            HearingDate = e.HearingDate,
            ResolvedOnDate = e.ResolvedOnDate,
            CourtName = e.CourtName,
            CaseNumber = e.CaseNumber,
            Resolution = e.Resolution,
            Notes = e.Notes,
            EventCount = e.Events.Count,
            LatestEventDate = e.Events.Count == 0 ? null : e.Events.Max(evt => evt.EventDate),
            Events = includeEvents
                ? e.Events.OrderBy(evt => evt.EventDate).ThenBy(evt => evt.Id).Select(EvictionCaseEventResponse.FromEntity).ToList()
                : [],
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };
    }
}

public class EvictionCaseEventResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int EvictionCaseId { get; set; }
    public EvictionEventType EventType { get; set; }
    public DateTime EventDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string TestId => $"eviction-event-{Id}";

    public static EvictionCaseEventResponse FromEntity(EvictionCaseEvent e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        EvictionCaseId = e.EvictionCaseId,
        EventType = e.EventType,
        EventDate = e.EventDate,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public class EvictionCaseListResponse
{
    public IReadOnlyList<EvictionCaseResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class EvictionCaseListQuery : ListQuery
{
    [FromQuery(Name = "leaseId")]
    public int? LeaseId { get; set; }

    [FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "tenantId")]
    public int? TenantId { get; set; }

    [FromQuery(Name = "status")]
    public EvictionCaseStatus? Status { get; set; }
}

public class CreateEvictionCaseRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int LeaseId { get; set; }

    [EnumDataType(typeof(EvictionCaseStatus))]
    public EvictionCaseStatus Status { get; set; } = EvictionCaseStatus.Filed;

    public DateTime? FiledOnDate { get; set; }
    public DateTime? HearingDate { get; set; }

    [MaxLength(200)]
    public string? CourtName { get; set; }

    [MaxLength(100)]
    public string? CaseNumber { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }
}

public class UpdateEvictionCaseRequest
{
    [EnumDataType(typeof(EvictionCaseStatus))]
    public EvictionCaseStatus? Status { get; set; }

    public DateTime? FiledOnDate { get; set; }
    public DateTime? HearingDate { get; set; }
    public DateTime? ResolvedOnDate { get; set; }

    [MaxLength(200)]
    public string? CourtName { get; set; }

    [MaxLength(100)]
    public string? CaseNumber { get; set; }

    [MaxLength(500)]
    public string? Resolution { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }
}

public class CreateEvictionCaseEventRequest
{
    [Required]
    [EnumDataType(typeof(EvictionEventType))]
    public EvictionEventType EventType { get; set; }

    [Required]
    public DateTime EventDate { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }
}
