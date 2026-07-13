namespace RentalCommand.Core.Entities;

/// <summary>
/// A registered push notification token for one user's device. Supports FCM (Android),
/// APNs (iOS), and web push. Registration is an upsert by <see cref="Token"/> — the same
/// physical device re-registering does not create a duplicate row.
/// </summary>
public class DeviceToken
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
