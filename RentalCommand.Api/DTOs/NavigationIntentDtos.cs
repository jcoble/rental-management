using RentalCommand.Core.Navigation;

namespace RentalCommand.Api.DTOs;

public sealed class NavigationResourceDto
{
    public string Kind { get; set; } = string.Empty;
    public int Id { get; set; }
}

public sealed class NavigationIntentDto
{
    public NavigationExperience Experience { get; set; }
    public NavigationDestination Destination { get; set; }
    public int AccessContextId { get; set; }
    public long AccessRevision { get; set; }
    public NavigationResourceDto? Resource { get; set; }
    public NavigationResourceDto? ParentResource { get; set; }
    public NavigationResourceDto? ChildResource { get; set; }
    public NavigationAction Action { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public NavigationDestination FallbackDestination { get; set; }
}

public static class NavigationIntentDtoMapper
{
    public static NavigationIntentDto ToDto(NavigationIntent intent) => new()
    {
        Experience = intent.Experience,
        Destination = intent.Destination,
        AccessContextId = intent.AccessContextId,
        AccessRevision = intent.AccessRevision,
        Resource = Resource(intent.ResourceKind, intent.ResourceId),
        ParentResource = Resource(intent.ParentResourceKind, intent.ParentResourceId),
        ChildResource = Resource(intent.ChildResourceKind, intent.ChildResourceId),
        Action = intent.Action,
        ExpiresAtUtc = intent.ExpiresAtUtc,
        FallbackDestination = intent.FallbackDestination,
    };

    private static NavigationResourceDto? Resource(string? kind, int? id) =>
        string.IsNullOrWhiteSpace(kind) || id is null or <= 0
            ? null
            : new NavigationResourceDto { Kind = kind, Id = id.Value };
}
