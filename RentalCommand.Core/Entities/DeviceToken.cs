using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A registered push notification token for one user's device. Supports FCM (Android),
/// APNs (iOS), and web push. Registration is an upsert by the selected workspace plus
/// <see cref="Token"/> so the same physical device can receive notifications for each
/// workspace it signs into without duplicating rows inside one workspace.
/// </summary>
public class DeviceToken : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>The <see cref="ApplicationUser"/> id that registered the device.</summary>
    public int UserId { get; set; }

    /// <summary>FCM/APNs registration token (up to 500 chars).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>"ios" | "android" | "web"</summary>
    public string Platform { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public Portfolio? Portfolio { get; set; }
}
