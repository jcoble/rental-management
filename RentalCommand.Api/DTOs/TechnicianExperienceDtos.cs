using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed class TechnicianAssignmentQuery : ListQuery
{
    public bool OpenOnly { get; set; }
    public WorkOrderStatus? Status { get; set; }
    public DateTimeOffset? ScheduledFrom { get; set; }
    public DateTimeOffset? ScheduledTo { get; set; }
}

public sealed record TechnicianAssignmentListItem(
    int Id,
    string Title,
    string Category,
    WorkOrderStatus Status,
    string Address,
    string? Unit,
    DateTime? ScheduledForUtc,
    DateTime? ScheduledWindowEndUtc,
    DateTime UpdatedAtUtc,
    int UnreadMessageCount);

public sealed record TechnicianAssignmentPage(
    IReadOnlyList<TechnicianAssignmentListItem> Items,
    int TotalCount,
    int Skip,
    int Take);

public sealed record TechnicianTimelineItem(
    int Id,
    WorkOrderStatus? FromStatus,
    WorkOrderStatus ToStatus,
    string? Note,
    string? ChangedBy,
    DateTime CreatedAtUtc);

public sealed record TechnicianWorkEntryDto(
    int Id,
    TechnicianWorkEntryKind Kind,
    string? Note,
    decimal? Quantity,
    string? Unit,
    int? PhotoFileId,
    DateTime OccurredAtUtc,
    DateTime CreatedAtUtc);

public sealed record TechnicianConversationMessageDto(
    int Id,
    string Sender,
    string Body,
    DateTime CreatedAtUtc);

public sealed record TechnicianAssignmentDetail(
    int Id,
    string Title,
    string Description,
    string Category,
    WorkOrderStatus Status,
    DateTime RequestedAtUtc,
    DateTime? ScheduledForUtc,
    DateTime? ScheduledWindowEndUtc,
    DateTime? CompletedAtUtc,
    DateTime UpdatedAtUtc,
    string Address,
    string? Unit,
    string? AccessInstructions,
    string? ContactName,
    string? ContactPhone,
    string? ContactEmail,
    int? ConversationId,
    IReadOnlyList<TechnicianTimelineItem> Timeline,
    IReadOnlyList<TechnicianWorkEntryDto> Entries,
    IReadOnlyList<TechnicianConversationMessageDto> Messages);

public sealed class RecordTechnicianWorkEntryRequest : IValidatableObject
{
    public TechnicianWorkEntryKind Kind { get; set; }

    [MaxLength(2000)]
    public string? Note { get; set; }

    [Range(typeof(decimal), "0.001", "999999999")]
    public decimal? Quantity { get; set; }

    [MaxLength(40)]
    public string? Unit { get; set; }

    [Range(1, int.MaxValue)]
    public int? PhotoFileId { get; set; }

    public DateTimeOffset? OccurredAt { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Kind == TechnicianWorkEntryKind.Note && string.IsNullOrWhiteSpace(Note))
            yield return new ValidationResult("A note is required.", [nameof(Note)]);
        if (Kind is TechnicianWorkEntryKind.Time or TechnicianWorkEntryKind.Material &&
            (Quantity is null or <= 0 || string.IsNullOrWhiteSpace(Unit)))
            yield return new ValidationResult("A positive quantity and unit are required.", [nameof(Quantity), nameof(Unit)]);
        if (Kind == TechnicianWorkEntryKind.Photo && PhotoFileId is null)
            yield return new ValidationResult("An uploaded photo is required.", [nameof(PhotoFileId)]);
    }
}

public sealed class TechnicianConversationMessageRequest
{
    [Required, MaxLength(4000)]
    public string Body { get; set; } = string.Empty;
}
