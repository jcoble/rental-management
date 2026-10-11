namespace RentalCommand.Core.Configuration;

/// <summary>
/// Independent signing and lifetime policy for deterministic atomic-session refresh credentials.
/// The signing key is supplied through configuration (normally an environment secret) and is never
/// written to the database, command receipt, audit log, or outbox.
/// </summary>
public sealed class AtomicAuthSessionCredentialOptions
{
    public const string SectionName = "AuthSessionCredentials";

    /// <summary>Base64-encoded signing key containing at least 256 bits of entropy.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int CredentialLifetimeDays { get; set; } = 7;
    public int FamilyAbsoluteLifetimeDays { get; set; } = 30;
    public int SessionLifetimeDays { get; set; } = 30;
}
