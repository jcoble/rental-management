using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public class EvictionCaseResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public string? RelationshipNumber { get; set; }
    public int? LeaseAgreementId { get; set; }
    public string? AgreementNumber { get; set; }
    public int PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public int UnitId { get; set; }
    public string? UnitNumber { get; set; }
    public IReadOnlyList<EvictionCaseRespondentResponse> Respondents { get; set; } = [];
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

}

public class EvictionCaseRespondentResponse
{
    public int LeaseManagementPartyId { get; set; }
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;

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
    [FromQuery(Name = "leaseManagementId")]
    public int? LeaseManagementId { get; set; }

    [FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "leaseManagementPartyId")]
    public int? LeaseManagementPartyId { get; set; }

    [FromQuery(Name = "status")]
    public EvictionCaseStatus? Status { get; set; }
}

public class CreateEvictionCaseRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int LeaseManagementId { get; set; }

    [Range(1, int.MaxValue)]
    public int? LeaseAgreementId { get; set; }

    [Required]
    [MinLength(1)]
    public IReadOnlyList<int> RespondentLeaseManagementPartyIds { get; set; } = [];

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
