using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Per-(<see cref="PortfolioId"/>, <see cref="NotificationType"/>) channel matrix: which of
/// In-app / Email / SMS the landlord wants a given automation event delivered on. Mirrors
/// EdiPlatform's <c>NotificationPreference</c> but adds the third (SMS) channel and is
/// portfolio-scoped rather than user-scoped. A missing row falls back to service-resolved
/// defaults, so rows are created lazily on first save — never pre-seeded.
/// Unique on (<see cref="PortfolioId"/>, <see cref="NotificationType"/>).
/// </summary>
public class NotificationPreference
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public NotificationType NotificationType { get; set; }

    /// <summary>Deliver this event as an in-app notification.</summary>
    public bool EnableInApp { get; set; } = true;

    /// <summary>Deliver this event by email.</summary>
    public bool EnableEmail { get; set; } = true;

    /// <summary>Deliver this event by SMS (off by default until a provider is configured).</summary>
    public bool EnableSms { get; set; }

    /// <summary>
    /// Deliver this event as a push notification to the landlord's registered mobile devices
    /// (FCM/APNs). On by default for in-app-style events so the phone-first product can interrupt;
    /// the actual send is a no-op until a push provider credential is configured (fail-soft).
    /// </summary>
    public bool EnablePush { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
}
