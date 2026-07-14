using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed class LeasingTodayResponse
{
    public int ApplicationsToReview { get; init; }
    public int ListingsNeedingAttention { get; init; }
    public int ShowingsToday { get; init; }
    public int UpcomingMoveIns { get; init; }
    public int UnreadConversations { get; init; }
}

public sealed class LeasingPipelineItemResponse
{
    public string Kind { get; init; } = string.Empty;
    public int RecordId { get; init; }
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? PropertyName { get; init; }
    public string? UnitNumber { get; init; }
    public string Stage { get; init; } = string.Empty;
    public DateTime? NextActionAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class LeasingPipelinePageResponse
{
    public IReadOnlyList<LeasingPipelineItemResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class LeasingRentalResponse
{
    public int PropertyId { get; init; }
    public int UnitId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public string UnitNumber { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int? ListingId { get; init; }
    public RentalListingStatus? ListingStatus { get; init; }
    public string? ListingHeadline { get; init; }
    public decimal? AskingRent { get; init; }
    public DateTime? AvailableOn { get; init; }
    public int OpenApplicationCount { get; init; }
    public DateTime? NextShowingAtUtc { get; init; }
}

public sealed class LeasingRentalPageResponse
{
    public IReadOnlyList<LeasingRentalResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class LeasingCalendarItemResponse
{
    public int Id { get; init; }
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
    public int? RentalApplicationId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ProspectName { get; init; }
    public string? PropertyName { get; init; }
    public string? UnitNumber { get; init; }
    public AppointmentType Type { get; init; }
    public AppointmentStatus Status { get; init; }
    public DateTime ScheduledStart { get; init; }
    public DateTime? ScheduledEnd { get; init; }
}

public sealed class LeasingCalendarPageResponse
{
    public IReadOnlyList<LeasingCalendarItemResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class LeasingInboxItemResponse
{
    public int Id { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string? PropertyName { get; init; }
    public string? LastMessagePreview { get; init; }
    public DateTime LastMessageAt { get; init; }
    public int UnreadCount { get; init; }
}

public sealed class LeasingInboxPageResponse
{
    public IReadOnlyList<LeasingInboxItemResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}
