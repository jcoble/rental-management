using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.DTOs;

public class NotificationResponse
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public NavigationIntentDto? NavigationIntent { get; set; }
    public string? RelatedEntityType { get; set; }
    public int? RelatedEntityId { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }

    public static NotificationResponse FromEntity(Notification notification) => new()
    {
        Id = notification.Id,
        Type = notification.Type,
        Title = notification.Title,
        Message = notification.Message,
        Severity = notification.Severity,
        NavigationIntent = notification.NavigationExperience is not null
            && notification.NavigationDestination is not null
            && notification.NavigationAccessContextId is not null
            && notification.NavigationAccessRevision is not null
            && notification.NavigationAction is not null
            && notification.NavigationExpiresAtUtc is not null
            && notification.NavigationFallbackDestination is not null
                ? new NavigationIntentDto
                {
                    Experience = notification.NavigationExperience.Value,
                    Destination = notification.NavigationDestination.Value,
                    AccessContextId = notification.NavigationAccessContextId.Value,
                    AccessRevision = notification.NavigationAccessRevision.Value,
                    Resource = Resource(
                        notification.NavigationResourceKind,
                        notification.NavigationResourceId),
                    ParentResource = Resource(
                        notification.NavigationParentResourceKind,
                        notification.NavigationParentResourceId),
                    ChildResource = Resource(
                        notification.NavigationChildResourceKind,
                        notification.NavigationChildResourceId),
                    Action = notification.NavigationAction.Value,
                    ExpiresAtUtc = notification.NavigationExpiresAtUtc.Value,
                    FallbackDestination = notification.NavigationFallbackDestination.Value,
                }
                : null,
        RelatedEntityType = notification.RelatedEntityType,
        RelatedEntityId = notification.RelatedEntityId,
        IsRead = false,
        CreatedAt = notification.CreatedAt,
    };

    private static NavigationResourceDto? Resource(string? kind, int? id) =>
        string.IsNullOrWhiteSpace(kind) || id is null or <= 0
            ? null
            : new NavigationResourceDto { Kind = kind, Id = id.Value };
}

public sealed record UnreadCountResponse(int Count);

public class CreateBroadcastNotificationRequest
{
    [Required]
    [MaxLength(160)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string Message { get; set; } = string.Empty;

    [MaxLength(40)]
    public string Severity { get; set; } = "Info";

}
