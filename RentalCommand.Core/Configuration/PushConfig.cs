namespace RentalCommand.Core.Configuration;

/// <summary>
/// Firebase Cloud Messaging (FCM HTTP v1) configuration for outbound push notifications.
/// Placeholder-safe: when no service-account credential is present <see cref="Enabled"/> is
/// false and the push sender no-ops with a suppression log, so the whole notification rail
/// keeps working before Firebase is set up.
/// </summary>
public sealed class PushConfig
{
    public const string SectionName = "Push";

    /// <summary>The Firebase project id (the FCM v1 send endpoint is project-scoped).</summary>
    public string? FirebaseProjectId { get; set; }

    /// <summary>
    /// Inline service-account JSON (the contents of the Firebase service-account key file). Set
    /// this OR <see cref="ServiceAccountJsonPath"/>. Kept out of source — supplied via env/secret.
    /// </summary>
    public string? ServiceAccountJson { get; set; }

    /// <summary>Path to a service-account JSON key file on disk (alternative to inline JSON).</summary>
    public string? ServiceAccountJsonPath { get; set; }

    /// <summary>
    /// True only when a project id AND a credential (inline JSON or a path) are present. Until then
    /// the push sender suppresses (logs) instead of sending — the app and the rest of the
    /// notification matrix work unchanged.
    /// </summary>
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(FirebaseProjectId)
        && (!string.IsNullOrWhiteSpace(ServiceAccountJson) || !string.IsNullOrWhiteSpace(ServiceAccountJsonPath));
}
