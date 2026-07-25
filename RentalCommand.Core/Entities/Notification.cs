using RentalCommand.Core.Navigation;

namespace RentalCommand.Core.Entities;

public class Notification : Interfaces.IAuditable, Interfaces.IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? UserId { get; set; }
    public string Type { get; set; } = "System";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public NavigationExperience? NavigationExperience { get; set; }
    public NavigationDestination? NavigationDestination { get; set; }
    public int? NavigationAccessContextId { get; set; }
    public long? NavigationAccessRevision { get; set; }
    public string? NavigationResourceKind { get; set; }
    public int? NavigationResourceId { get; set; }
    public string? NavigationParentResourceKind { get; set; }
    public int? NavigationParentResourceId { get; set; }
    public string? NavigationChildResourceKind { get; set; }
    public int? NavigationChildResourceId { get; set; }
    public NavigationAction? NavigationAction { get; set; }
    public DateTime? NavigationExpiresAtUtc { get; set; }
    public NavigationDestination? NavigationFallbackDestination { get; set; }
    public string? RelatedEntityType { get; set; }
    public int? RelatedEntityId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public ICollection<NotificationReadState> ReadStates { get; set; } = new List<NotificationReadState>();
}
