namespace RentalCommand.Core.Configuration;

/// <summary>
/// Configuration for Google OAuth sign-in. Bind from <c>Authentication:Google</c>.
/// Keys match the EdiPlatform sister app so credentials are reusable across projects.
/// When <see cref="Enabled"/> is false the endpoint returns 501 and no external calls are made.
/// </summary>
public class GoogleAuthOptions
{
    public const string SectionName = "Authentication:Google";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>True only when both ClientId and ClientSecret are configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
