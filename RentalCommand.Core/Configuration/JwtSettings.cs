namespace RentalCommand.Core.Configuration;

/// <summary>
/// JWT access-token settings. Bound from the "Jwt" configuration section.
/// </summary>
public class JwtSettings
{
    public const string SectionName = "Jwt";

    /// <summary>Symmetric signing key for access tokens.</summary>
    public string SecretKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    /// <summary>Access-token lifetime in minutes.</summary>
    public int AccessTokenExpirationMinutes { get; set; } = 15;
}
