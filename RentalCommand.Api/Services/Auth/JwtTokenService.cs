using System.IdentityModel.Tokens.Jwt;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

public class TokenResult
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime AccessTokenExpiration { get; set; }
    public DateTime RefreshTokenExpiration { get; set; }
}

public interface IJwtTokenService
{
    /// <summary>Issues a 15-min access token + a 7-day single-use refresh token (persisted, hashed).</summary>
    Task<TokenResult> GenerateTokensAsync(ApplicationUser user, IList<string> roles, string? ipAddress = null, string? userAgent = null);

    /// <summary>Rotates a presented refresh token: the old token is marked used+revoked and a new pair is issued.</summary>
    Task<TokenResult?> RefreshTokenAsync(string refreshToken, string? ipAddress = null, string? userAgent = null);

    /// <summary>Revokes a refresh token (logout). Returns false if the token was unknown.</summary>
    Task<bool> RevokeRefreshTokenAsync(string refreshToken);

    /// <summary>Validates an access token's signature/issuer/audience and returns its principal, or null.</summary>
    ClaimsPrincipal? ValidateAccessToken(string token);
}

/// <summary>
/// Issues and rotates JWTs for the int-keyed <see cref="ApplicationUser"/>. The access token carries
/// <c>sub</c>/<see cref="ClaimTypes.NameIdentifier"/> as the int user id (string-encoded), the user's
/// roles, and a <c>portfolioId</c> claim when the user is scoped to a portfolio. Refresh tokens are
/// random 64-byte values stored only as SHA-256 hashes; they are single-use and rotated on every refresh.
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    // Single-flight only: collapse truly concurrent refreshes of the SAME token into one DB rotation
    // so a benign client double-submit doesn't trip the reuse detector. We deliberately do NOT cache
    // and re-serve completed results (that would hand the same bearer tokens to a replay/attacker);
    // clients already serialize their own refreshes (web/src/lib/server/token-refresh.ts + the mobile
    // Dio interceptor), so the server can safely treat a presented-already-rotated token as theft.
    private static readonly ConcurrentDictionary<string, Lazy<Task<TokenResult?>>> InFlightRefreshes = new();

    private readonly JwtSettings _settings;
    private readonly RentalCommandDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<JwtTokenService> _logger;
    private readonly TokenValidationParameters _tokenValidationParameters;

    public JwtTokenService(
        IOptions<JwtSettings> settings,
        RentalCommandDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        ILogger<JwtTokenService> logger)
    {
        _settings = settings.Value;
        _dbContext = dbContext;
        _userManager = userManager;
        _logger = logger;

        var key = Encoding.UTF8.GetBytes(_settings.SecretKey);
        _tokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = true,
            ValidIssuer = _settings.Issuer,
            ValidateAudience = true,
            ValidAudience = _settings.Audience,
            // Lifetime validation is intentionally relaxed here so we can inspect claims during
            // refresh flows; the JwtBearer middleware (Program.cs) enforces lifetime on the API surface.
            ValidateLifetime = false,
            ClockSkew = TimeSpan.Zero
        };
    }

    public async Task<TokenResult> GenerateTokensAsync(ApplicationUser user, IList<string> roles, string? ipAddress = null, string? userAgent = null)
    {
        var accessTokenExpiration = DateTime.UtcNow.AddMinutes(_settings.AccessTokenExpirationMinutes);
        var refreshTokenExpiration = DateTime.UtcNow.AddDays(_settings.RefreshTokenExpirationDays);

        var accessToken = GenerateAccessToken(user, roles, accessTokenExpiration);
        var refreshToken = await GenerateRefreshTokenAsync(user, refreshTokenExpiration, ipAddress, userAgent);

        return new TokenResult
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            AccessTokenExpiration = accessTokenExpiration,
            RefreshTokenExpiration = refreshTokenExpiration
        };
    }

    private string GenerateAccessToken(ApplicationUser user, IList<string> roles, DateTime expiration)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            // INT user key: sub / NameIdentifier are the int id, string-encoded. Parsed back with int.Parse.
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("displayName", user.DisplayName ?? user.Email ?? string.Empty),
        };

        if (user.PortfolioId.HasValue)
        {
            claims.Add(new Claim("portfolioId", user.PortfolioId.Value.ToString()));
        }

        if (user.OwnerEntityId.HasValue)
        {
            claims.Add(new Claim("ownerEntityId", user.OwnerEntityId.Value.ToString()));
        }

        if (user.TenantId.HasValue)
        {
            claims.Add(new Claim("tenantId", user.TenantId.Value.ToString()));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            expires: expiration,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<string> GenerateRefreshTokenAsync(ApplicationUser user, DateTime expiration, string? ipAddress, string? userAgent)
    {
        // Cryptographically secure random token; only its hash is persisted.
        var randomBytes = RandomNumberGenerator.GetBytes(64);
        var refreshToken = Convert.ToBase64String(randomBytes);

        var entity = new RefreshToken
        {
            UserId = user.Id,
            PortfolioId = user.PortfolioId,
            // Token is stored empty by design; only the hash is used for lookups so a DB leak
            // does not expose usable refresh tokens. Kept as a column for forward compatibility.
            Token = string.Empty,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = expiration,
            IssuedAt = DateTime.UtcNow,
            IsRevoked = false,
            IsUsed = false,
            IpAddress = ipAddress,
            UserAgent = userAgent
        };

        _dbContext.RefreshTokens.Add(entity);
        await _dbContext.SaveChangesAsync();

        return refreshToken;
    }

    public async Task<TokenResult?> RefreshTokenAsync(string refreshToken, string? ipAddress = null, string? userAgent = null)
    {
        var tokenHash = HashToken(refreshToken);

        var refreshWork = InFlightRefreshes.GetOrAdd(
            tokenHash,
            _ => new Lazy<Task<TokenResult?>>(
                () => RefreshTokenCoreAsync(tokenHash, ipAddress, userAgent),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await refreshWork.Value;
        }
        finally
        {
            InFlightRefreshes.TryRemove(tokenHash, out _);
        }
    }

    private async Task<TokenResult?> RefreshTokenCoreAsync(string tokenHash, string? ipAddress, string? userAgent)
    {
        var storedToken = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash);

        if (storedToken == null)
        {
            _logger.LogWarning("Refresh token not found");
            return null;
        }

        // Single-use rotation with token-family invalidation: presenting an already-rotated
        // (used) or revoked token signals theft/replay of a leaked token, so we revoke EVERY
        // active refresh token for the user and force re-authentication. Benign concurrent
        // refreshes never reach here because they share the single-flight above and clients
        // serialize their own refreshes.
        if (storedToken.IsUsed || storedToken.IsRevoked)
        {
            _logger.LogWarning(
                "Refresh token reuse/revoked detected for user {UserId}; revoking the entire token family.",
                storedToken.UserId);
            await RevokeTokenFamilyAsync(storedToken.UserId);
            return null;
        }

        if (storedToken.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogWarning("Refresh token expired for user {UserId}", storedToken.UserId);
            storedToken.IsRevoked = true;
            await _dbContext.SaveChangesAsync();
            return null;
        }

        var user = await _userManager.FindByIdAsync(storedToken.UserId.ToString());
        if (user == null)
        {
            _logger.LogWarning("User {UserId} not found for refresh token", storedToken.UserId);
            return null;
        }

        var roles = await _userManager.GetRolesAsync(user);

        // Rotate: mark the presented token used+revoked, then issue a fresh pair.
        storedToken.IsUsed = true;
        storedToken.IsRevoked = true;
        await _dbContext.SaveChangesAsync();

        return await GenerateTokensAsync(user, roles, ipAddress, userAgent);
    }

    /// <summary>
    /// Revokes every active refresh token for a user. Invoked when a single-use token is replayed,
    /// which indicates the token family is compromised; the user must re-authenticate.
    /// </summary>
    private async Task RevokeTokenFamilyAsync(int userId)
    {
        var activeTokens = await _dbContext.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked)
            .ToListAsync();

        if (activeTokens.Count == 0)
        {
            return;
        }

        foreach (var token in activeTokens)
        {
            token.IsRevoked = true;
            token.IsUsed = true;
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task<bool> RevokeRefreshTokenAsync(string refreshToken)
    {
        var tokenHash = HashToken(refreshToken);

        var storedToken = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash);

        if (storedToken == null)
        {
            return false;
        }

        storedToken.IsRevoked = true;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public ClaimsPrincipal? ValidateAccessToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, _tokenValidationParameters, out var validatedToken);

            if (validatedToken is JwtSecurityToken jwtToken &&
                jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
            {
                return principal;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Access token validation failed");
        }

        return null;
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }
}
