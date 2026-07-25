using RentalCommand.Core.Enums;
using RentalCommand.Core.Owners;

namespace RentalCommand.Api.DTOs;

public sealed class OwnerPortalOverviewResponse
{
    public int CurrentYear { get; init; }
    public int PropertyCount { get; init; }
    public int UnitCount { get; init; }
    public decimal DistributedThisYear { get; init; }
    public int PendingApprovalCount { get; init; }
    public int UnreadMessageCount { get; init; }
}

public sealed class OwnerPortalPropertyResponse
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public PropertyType PropertyType { get; init; }
    public PropertyStatus Status { get; init; }
    public string AddressLine1 { get; init; } = string.Empty;
    public string? AddressLine2 { get; init; }
    public string City { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public int UnitCount { get; init; }
}

public sealed class OwnerPortalPropertyPageResponse
{
    public IReadOnlyList<OwnerPortalPropertyResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class OwnerPortalItemResponse
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public bool IsRead { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class OwnerPortalItemPageResponse
{
    public IReadOnlyList<OwnerPortalItemResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class DecideOwnerApprovalRequest
{
    public OwnerApprovalDecision Decision { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(1000)]
    public string? Note { get; set; }
}

public sealed class ReplyToOwnerMessageRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(4000)]
    public string Body { get; set; } = string.Empty;
}

public sealed record OwnerPortalCommandResponse(
    int SourceNotificationId,
    int OwnerEntityId,
    IReadOnlyList<int> StaffNotificationIds,
    DateTime RecordedAtUtc,
    bool Replayed);

public sealed class OwnerPortalDistributionResponse
{
    public int Id { get; init; }
    public int OwnerEntityId { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public int? PropertyId { get; init; }
    public string? PropertyName { get; init; }
    public DateTime Date { get; init; }
    public decimal Amount { get; init; }
    public DistributionMethod Method { get; init; }
    public string? Memo { get; init; }
}

public sealed class OwnerPortalDistributionPageResponse
{
    public IReadOnlyList<OwnerPortalDistributionResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}
