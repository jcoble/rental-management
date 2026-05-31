using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Auth;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

public static class ApiKeyAuthenticationDefaults
{
    public const string AuthenticationScheme = "ApiKey";
}

/// <summary>
/// Authenticates requests carrying an <c>X-API-Key</c> header. The key's first 8 characters form a
/// prefix used for fast lookup; the full key is SHA-256 hashed (Base64) and compared against the
/// configured hash. On success a principal carrying the key's name and optional portfolio claim is
/// issued. Phase 0 sources keys from <see cref="ApiKeySettings"/> (configuration); the prefix+hash
/// contract is unchanged if keys later move to a database table.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private readonly ApiKeySettings _settings;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<ApiKeySettings> apiKeySettings)
        : base(options, logger, encoder)
    {
        _settings = apiKeySettings.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var providedApiKey = apiKeyHeader.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providedApiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("API key is empty"));
        }

        if (providedApiKey.Length < 8)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key format"));
        }

        var keyPrefix = providedApiKey[..8];
        var keyHash = HashApiKey(providedApiKey);

        var match = _settings.Keys.FirstOrDefault(k =>
            k.Prefix == keyPrefix &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(k.Hash),
                Encoding.UTF8.GetBytes(keyHash)));

        if (match == null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, match.Name),
            new(ClaimTypes.Name, match.Name),
            new("apiKeyName", match.Name)
        };

        if (match.PortfolioId.HasValue)
        {
            claims.Add(new Claim("portfolioId", match.PortfolioId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>SHA-256 hashes an API key and returns the Base64 representation.</summary>
    public static string HashApiKey(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Generates a URL-safe random API key plus its prefix and hash (for provisioning).</summary>
    public static (string Key, string Hash, string Prefix) GenerateApiKey()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var key = Convert.ToBase64String(keyBytes).Replace("/", "_").Replace("+", "-")[..43];
        var prefix = key[..8];
        var hash = HashApiKey(key);
        return (key, hash, prefix);
    }
}
