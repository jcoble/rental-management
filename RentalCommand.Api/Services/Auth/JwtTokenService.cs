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

    /// <summary>
    /// Revokes every active refresh token for the user who owns the presented token (sign out
    /// everywhere). Used by explicit user logout so signing out ends all of that user's sessions, not
    /// just the presenting device. Returns false if the token was unknown.
    /// </summary>
    Task<bool> RevokeRefreshTokenFamilyAsync(string refreshToken);

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
    // Single-flight: collapse truly concurrent refreshes of the SAME token into one DB rotation
    // so a benign client double-submit doesn't trip the reuse detector.
    private static readonly ConcurrentDictionary<string, Lazy<Task<TokenResult?>>> InFlightRefreshes = new();

    // Reuse grace window. A legitimate session has several independent refresh triggers (SSR
    // /auth/me 401, client 401-retry, proactive pre-expiry refresh, SignalR accessTokenFactory,
    // and the mobile Dio interceptor — a SEPARATE process from the web node server) that can each
    // read the same refresh cookie before the rotated one has propagated. The in-flight map only
    // dedupes calls that overlap in time; a trigger that fires shortly AFTER the first rotation
    // completed would present the now-used token and, without this grace, trip the family-revoke
    // and bounce the user. So for a brief window after rotating a token we remember the successor
    // it produced and re-serve THAT SAME successor idempotently if the same token is presented
    // again. This does NOT weaken stolen-token protection: a replay outside the window (or of a
    // token this process never rotated) still hits the hard family-revoke below.
    private static readonly ConcurrentDictionary<string, (TokenResult Result, DateTime ExpiresAt)> RecentlyRotated = new();
    private static readonly TimeSpan ReuseGraceWindow = TimeSpan.FromSeconds(30);
    private static DateTime _lastGracePrune = DateTime.UtcNow;

    // Test seam: clear the process-static in-flight/grace maps so a test starts from a known state and
    // its concurrency assertions exercise the DB-level atomic claim rather than a leftover in-process entry.
    internal static void ResetInProcessStateForTests()
    {
        InFlightRefreshes.Clear();
        RecentlyRotated.Clear();
    }

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

        var now = DateTime.UtcNow;

        // Single-use rotation with token-family invalidation: presenting an already-rotated
        // (used) or revoked token normally signals theft/replay of a leaked token, so we revoke
        // EVERY active refresh token for the user and force re-authentication.
        //
        // EXCEPTION — the reuse grace window (see RefreshToken.GraceExpiresAt): if this token was
        // rotated moments ago, the presenter is almost certainly a legitimate straggler trigger
        // (another tab, the SSR refresh, the mobile interceptor in a SEPARATE process) that read
        // the cookie just before the rotated one landed. The in-process map re-serves the EXACT
        // successor; across instances the persisted GraceExpiresAt lets any replica re-serve a fresh
        // valid pair. Either way we do NOT nuke the family inside the grace window.
        if (storedToken.IsUsed || storedToken.IsRevoked)
        {
            return await TryServeWithinGraceAsync(storedToken, tokenHash, now, ipAddress, userAgent);
        }

        if (storedToken.ExpiresAt < now)
        {
            _logger.LogWarning("Refresh token expired for user {UserId}", storedToken.UserId);
            storedToken.IsRevoked = true;
            await _dbContext.SaveChangesAsync();
            return null;
        }

        // Atomic, cross-process claim: flip used+revoked AND stamp the grace deadline in a single
        // conditional UPDATE that only matches a still-live row. Exactly one concurrent refresh of the
        // same token wins (1 row affected); the rest see 0 rows and fall through to the grace path,
        // so two instances can never both rotate the same token (no lost update / double-rotate) and a
        // benign concurrent double-submit never falsely trips the family-revoke.
        var graceDeadline = now.Add(ReuseGraceWindow);
        var claimed = await _dbContext.RefreshTokens
            .Where(rt => rt.Id == storedToken.Id && !rt.IsUsed && !rt.IsRevoked)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(rt => rt.IsUsed, true)
                .SetProperty(rt => rt.IsRevoked, true)
                .SetProperty(rt => rt.GraceExpiresAt, graceDeadline));

        if (claimed == 0)
        {
            // Lost the race to a concurrent rotation on this or another instance. Re-read and re-serve
            // within the grace window rather than revoking the family.
            await _dbContext.Entry(storedToken).ReloadAsync();
            return await TryServeWithinGraceAsync(storedToken, tokenHash, now, ipAddress, userAgent);
        }

        // Keep the tracked entity consistent with the conditional UPDATE we just executed.
        storedToken.IsUsed = true;
        storedToken.IsRevoked = true;
        storedToken.GraceExpiresAt = graceDeadline;

        var user = await _userManager.FindByIdAsync(storedToken.UserId.ToString());
        if (user == null)
        {
            _logger.LogWarning("User {UserId} not found for refresh token", storedToken.UserId);
            return null;
        }

        var roles = await _userManager.GetRolesAsync(user);
        var newTokens = await GenerateTokensAsync(user, roles, ipAddress, userAgent);

        // Remember the successor briefly so a same-process straggler that re-presents this exact
        // (now-rotated) token within the grace window gets the SAME pair back. The persisted
        // GraceExpiresAt covers the cross-process case. See RecentlyRotated / ReuseGraceWindow above.
        RememberRotation(tokenHash, newTokens);

        return newTokens;
    }

    /// <summary>
    /// Handles a re-presented token that is already used/revoked. Returns a token pair (no family revoke)
    /// when the presentation falls inside the reuse grace window — preferring the in-process map's exact
    /// successor, otherwise minting a fresh valid pair off the persisted <see cref="RefreshToken.GraceExpiresAt"/>
    /// so the grace is correct across API instances. Outside the grace window this is genuine reuse/theft:
    /// it revokes the entire token family and returns null.
    /// </summary>
    private async Task<TokenResult?> TryServeWithinGraceAsync(
        RefreshToken storedToken, string tokenHash, DateTime now, string? ipAddress, string? userAgent)
    {
        // Same-process fast path: hand back the EXACT successor we already minted (idempotent).
        if (TryGetRecentlyRotated(tokenHash, out var graceResult))
        {
            _logger.LogInformation(
                "Refresh token presented again within the in-process reuse grace window for user {UserId}; re-serving the rotated successor (no family revoke).",
                storedToken.UserId);
            return graceResult;
        }

        // Cross-process grace: the row was rotated very recently (by this or another instance) and is
        // still inside its persisted grace window — treat as a benign straggler and mint a fresh pair.
        if (storedToken.GraceExpiresAt is { } deadline && now < deadline)
        {
            var graceUser = await _userManager.FindByIdAsync(storedToken.UserId.ToString());
            if (graceUser == null)
            {
                _logger.LogWarning("User {UserId} not found while serving refresh grace", storedToken.UserId);
                return null;
            }

            var graceRoles = await _userManager.GetRolesAsync(graceUser);
            var graceTokens = await GenerateTokensAsync(graceUser, graceRoles, ipAddress, userAgent);

            _logger.LogInformation(
                "Refresh token presented again within the persisted reuse grace window for user {UserId}; issuing a fresh pair across instances (no family revoke).",
                storedToken.UserId);
            return graceTokens;
        }

        _logger.LogWarning(
            "Refresh token reuse/revoked detected for user {UserId}; revoking the entire token family.",
            storedToken.UserId);
        await RevokeTokenFamilyAsync(storedToken.UserId);
        return null;
    }

    /// <summary>
    /// Records the successor token pair produced by rotating <paramref name="rotatedTokenHash"/>,
    /// keyed by that token's hash, valid for <see cref="ReuseGraceWindow"/>. Bounded by an
    /// opportunistic prune of expired entries so the map can't grow without limit.
    /// </summary>
    private static void RememberRotation(string rotatedTokenHash, TokenResult successor)
    {
        var now = DateTime.UtcNow;
        RecentlyRotated[rotatedTokenHash] = (successor, now.Add(ReuseGraceWindow));

        // Opportunistically evict expired entries (cheap, at most once per grace window).
        if (now - _lastGracePrune > ReuseGraceWindow)
        {
            _lastGracePrune = now;
            foreach (var kvp in RecentlyRotated)
            {
                if (now >= kvp.Value.ExpiresAt)
                {
                    RecentlyRotated.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    /// <summary>
    /// Returns the successor pair for a just-rotated token if it is still within the reuse grace
    /// window; otherwise false. Expired entries are removed.
    /// </summary>
    private static bool TryGetRecentlyRotated(string tokenHash, out TokenResult? result)
    {
        result = null;
        if (!RecentlyRotated.TryGetValue(tokenHash, out var entry))
        {
            return false;
        }

        if (DateTime.UtcNow >= entry.ExpiresAt)
        {
            RecentlyRotated.TryRemove(tokenHash, out _);
            return false;
        }

        result = entry.Result;
        return true;
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

    public async Task<bool> RevokeRefreshTokenFamilyAsync(string refreshToken)
    {
        var tokenHash = HashToken(refreshToken);

        // Resolve the owning user from the presented token, then revoke every active token for that
        // user — the same family-revoke path used on theft detection — so an explicit sign-out ends
        // sessions on all devices, not just the one that presented the cookie.
        var storedToken = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash);

        if (storedToken == null)
        {
            return false;
        }

        await RevokeTokenFamilyAsync(storedToken.UserId);
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
