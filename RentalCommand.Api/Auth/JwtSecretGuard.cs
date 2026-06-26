namespace RentalCommand.Api.Auth;

/// <summary>
/// Startup-time guard for the symmetric JWT signing secret. This is separate from Program.cs so the
/// security invariant can be unit-tested without booting the whole API.
/// </summary>
public static class JwtSecretGuard
{
    private const int MinimumSecretLength = 32;

    private static readonly string[] PlaceholderFragments =
    [
        "CHANGE_ME",
        "random_secret_value_here",
        "IN_PRODUCTION"
    ];

    public static void Validate(string? secretKey, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(secretKey) || secretKey.Length < MinimumSecretLength)
        {
            throw new InvalidOperationException(
                "Jwt:SecretKey is missing or too short (need >= 32 chars). Set it via configuration or a secret store.");
        }

        if (!isDevelopment && LooksLikePlaceholder(secretKey))
        {
            throw new InvalidOperationException(
                "Jwt:SecretKey is still an unedited placeholder. Set a real secret (env Jwt__SecretKey or a secret store) before deploying.");
        }
    }

    private static bool LooksLikePlaceholder(string secretKey) =>
        PlaceholderFragments.Any(fragment =>
            secretKey.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
