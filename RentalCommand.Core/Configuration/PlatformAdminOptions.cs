namespace RentalCommand.Core.Configuration;

/// <summary>
/// Platform super-admin allowlist (IA Wave 1, F6 / TSK-212). Operator-only endpoints
/// (Engine Health, etc.) resolve the canonical user subject to its current database email and
/// evaluate this allowlist. Bound from the "PlatformAdmin" configuration section.
///
/// When <see cref="Emails"/> is empty, no one is a platform admin (fail closed).
/// </summary>
public class PlatformAdminOptions
{
    public const string SectionName = "PlatformAdmin";

    /// <summary>Allowlisted super-admin emails (case-insensitive).</summary>
    public string[] Emails { get; set; } = System.Array.Empty<string>();
}
