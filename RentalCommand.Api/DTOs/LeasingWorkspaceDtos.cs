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
    public int? LeaseManagementId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public string UnitNumber { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int? ListingId { get; init; }
    public RentalListingStatus? ListingStatus { get; init; }
    public string? ListingHeadline { get; init; }
    public decimal? AskingRent { get; init; }
    public DateTime? AvailableOn { get; init; }
    public bool CanViewApplications { get; init; }
    public int OpenApplicationCount { get; init; }
    public bool CanViewShowings { get; init; }
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

public sealed class LeasingRentalDetailResponse
{
    public int PropertyId { get; init; }
    public int UnitId { get; init; }
    public int? LeaseManagementId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public string UnitNumber { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int? ListingId { get; init; }
    public RentalListingStatus? ListingStatus { get; init; }
    public string? ListingHeadline { get; init; }
    public string? ListingDescription { get; init; }
    public decimal? AskingRent { get; init; }
    public decimal? SecurityDeposit { get; init; }
    public DateTime? AvailableOn { get; init; }
    public string? LeaseTerms { get; init; }
    public string? PetPolicy { get; init; }
    public bool CanViewApplications { get; init; }
    public int OpenApplicationCount { get; init; }
    public bool CanViewShowings { get; init; }
    public DateTime? NextShowingAtUtc { get; init; }
}

public sealed class LeasingApplicationDetailResponse
{
    public int Id { get; init; }
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
    public string ApplicantName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? PropertyName { get; init; }
    public string? UnitNumber { get; init; }
    public ApplicationStatus Status { get; init; }
    public decimal? MonthlyIncome { get; init; }
    public DateTime? DesiredMoveInDate { get; init; }
    public string? Notes { get; init; }
    public bool ConsentGiven { get; init; }
    public DateTime SubmittedAtUtc { get; init; }
    public int? ApprovedTenantId { get; init; }
}

public sealed class LeasingAppointmentDetailResponse
{
    public int Id { get; init; }
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
    public int? RentalApplicationId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ProspectName { get; init; }
    public string? ProspectEmail { get; init; }
    public string? PropertyName { get; init; }
    public string? UnitNumber { get; init; }
    public AppointmentType Type { get; init; }
    public AppointmentStatus Status { get; init; }
    public DateTime ScheduledStart { get; init; }
    public DateTime? ScheduledEnd { get; init; }
    public string? AssignedTo { get; init; }
    public string? Notes { get; init; }
}

public sealed class LeasingConversationMessageResponse
{
    public int Id { get; init; }
    public ConversationSenderRole SenderRole { get; init; }
    public string Body { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

public sealed class LeasingConversationDetailResponse
{
    public int Id { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string? PropertyName { get; init; }
    public IReadOnlyList<LeasingConversationMessageResponse> Messages { get; init; } = [];
}

public sealed class LeasingMoveInDetailResponse
{
    public int Id { get; init; }
    public int PropertyId { get; init; }
    public int UnitId { get; init; }
    public string RelationshipNumber { get; init; } = string.Empty;
    public string TenantName { get; init; } = string.Empty;
    public string PropertyName { get; init; } = string.Empty;
    public string UnitNumber { get; init; } = string.Empty;
    public DateTime? PlannedPossessionAtUtc { get; init; }
    public bool AgreementFullyExecuted { get; init; }
    public bool PossessionGiven { get; init; }
}
