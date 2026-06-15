using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Result from a Google sign-in exchange. On failure <see cref="Success"/> is false and
/// <see cref="Error"/> contains a safe-to-log (not safe-to-return) message.
/// </summary>
public class GoogleAuthResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public LoginResponse? Response { get; set; }
    public TokenResult? Tokens { get; set; }

    public static GoogleAuthResult Ok(LoginResponse response, TokenResult tokens) =>
        new() { Success = true, Response = response, Tokens = tokens };

    public static GoogleAuthResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public interface IGoogleAuthService
{
    /// <summary>
    /// Exchanges a Google authorization <paramref name="code"/> for an id_token, validates it,
    /// finds or creates the local user, and issues our own JWT token pair.
    /// Returns a failure result when Google sign-in is not configured.
    /// </summary>
    Task<GoogleAuthResult> AuthenticateAsync(string code, string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// Native (mobile) sign-in: validates a Google <paramref name="idToken"/> obtained on-device
    /// (e.g. via <c>google_sign_in</c> configured with a <c>serverClientId</c>), then finds/creates
    /// the user and issues our own JWT pair. Skips the authorization-code exchange the web flow uses.
    /// </summary>
    Task<GoogleAuthResult> AuthenticateWithIdTokenAsync(string idToken, CancellationToken ct = default);
}

/// <summary>
/// Implements <see cref="IGoogleAuthService"/> by exchanging the code at Google's token endpoint,
/// validating the returned id_token via the tokeninfo endpoint, then finding or creating a local
/// <see cref="ApplicationUser"/> and issuing our JWT pair.
/// The entire flow is inert (returns 501-equivalent) when Google credentials are not configured.
/// </summary>
public sealed class GoogleAuthService : IGoogleAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IJwtTokenService _tokenService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GoogleAuthOptions _options;
    private readonly RentalCommandDbContext _db;
    private readonly Domain.ISelfOwnerProvisioner _selfOwnerProvisioner;
    private readonly ILogger<GoogleAuthService> _logger;

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string TokenInfoEndpoint = "https://oauth2.googleapis.com/tokeninfo";

    public GoogleAuthService(
        UserManager<ApplicationUser> userManager,
        IJwtTokenService tokenService,
        IHttpClientFactory httpClientFactory,
        IOptions<GoogleAuthOptions> options,
        RentalCommandDbContext db,
        Domain.ISelfOwnerProvisioner selfOwnerProvisioner,
        ILogger<GoogleAuthService> logger)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _db = db;
        _selfOwnerProvisioner = selfOwnerProvisioner;
        _logger = logger;
    }

    public async Task<GoogleAuthResult> AuthenticateAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return GoogleAuthResult.Fail("Google sign-in is not configured.");
        }

        try
        {
            // Step 1: Exchange authorization code for tokens.
            var idToken = await ExchangeCodeAsync(code, redirectUri, ct);
            if (idToken == null)
            {
                return GoogleAuthResult.Fail("Failed to exchange authorization code with Google.");
            }

            // Step 2: Validate the id_token and extract claims.
            var claims = await ValidateIdTokenAsync(idToken, ct);
            if (claims == null)
            {
                return GoogleAuthResult.Fail("Google id_token validation failed.");
            }

            // Steps 3-4 (find/create the user, provision, issue tokens) are shared with the
            // native id_token flow below.
            return await CompleteSignInAsync(claims, ct);
        }
        catch (Exception ex)
        {
            // Log detail internally; caller must NOT leak this to the response.
            _logger.LogError(ex, "Unexpected error during Google sign-in exchange.");
            return GoogleAuthResult.Fail("Unexpected error during Google sign-in.");
        }
    }

    public async Task<GoogleAuthResult> AuthenticateWithIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return GoogleAuthResult.Fail("Google sign-in is not configured.");
        }

        try
        {
            // Native clients (mobile) obtain the id_token on-device, so there is no authorization
            // code to exchange — validate the id_token directly, then run the shared completion.
            var claims = await ValidateIdTokenAsync(idToken, ct);
            if (claims == null)
            {
                return GoogleAuthResult.Fail("Google id_token validation failed.");
            }

            return await CompleteSignInAsync(claims, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Google id_token sign-in.");
            return GoogleAuthResult.Fail("Unexpected error during Google sign-in.");
        }
    }

    /// <summary>
    /// Shared completion for both the web authorization-code flow and the native id_token flow:
    /// validates the email claim, finds/creates the local user, provisions their portfolio + Admin
    /// role (idempotent), and issues our JWT pair.
    /// </summary>
    private async Task<GoogleAuthResult> CompleteSignInAsync(Dictionary<string, string> claims, CancellationToken ct)
    {
        if (!claims.TryGetValue("email", out var email) || string.IsNullOrWhiteSpace(email))
        {
            return GoogleAuthResult.Fail("Google id_token did not contain an email claim.");
        }

        var user = await FindOrCreateUserAsync(email, claims, ct);
        if (user == null)
        {
            return GoogleAuthResult.Fail($"Failed to find or create user for email {email}.");
        }

        // A brand-new Google user (or one created before this fix) needs a sandbox portfolio, the
        // Admin role (a self-service owner administers their own portfolio), and a UserAccount staff
        // row. Done BEFORE issuing tokens so the JWT carries portfolioId + roles. Each step is
        // idempotent, so this also back-fills users created before these fixes.
        await EnsureOwnerProvisioningAsync(user);

        var roles = await _userManager.GetRolesAsync(user);
        var tokens = await _tokenService.GenerateTokensAsync(user, roles);

        user.LastLoginAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        var response = new LoginResponse
        {
            AccessToken = tokens.AccessToken,
            AccessTokenExpiration = tokens.AccessTokenExpiration,
            User = new UserDto
            {
                Id = user.Id,
                Email = user.Email ?? string.Empty,
                DisplayName = user.DisplayName ?? user.Email ?? string.Empty,
                PortfolioId = user.PortfolioId,
                OwnerEntityId = user.OwnerEntityId,
                TenantId = user.TenantId,
                Roles = roles.ToList(),
                EmailVerified = user.EmailConfirmed
            }
        };

        return GoogleAuthResult.Ok(response, tokens);
    }

    private async Task<string?> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("GoogleAuth");

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(TokenEndpoint, formContent, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP request to Google token endpoint failed.");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Google token endpoint returned {StatusCode}. Body: {Body}", response.StatusCode, body);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("id_token", out var idTokenElement))
        {
            _logger.LogWarning("Google token response did not contain id_token.");
            return null;
        }

        return idTokenElement.GetString();
    }

    private async Task<Dictionary<string, string>?> ValidateIdTokenAsync(string idToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("GoogleAuth");

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"{TokenInfoEndpoint}?id_token={Uri.EscapeDataString(idToken)}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP request to Google tokeninfo endpoint failed.");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Google tokeninfo returned {StatusCode}. Body: {Body}", response.StatusCode, body);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            claims[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }

        // Verify aud matches our ClientId.
        if (!claims.TryGetValue("aud", out var aud) || aud != _options.ClientId)
        {
            _logger.LogWarning("Google tokeninfo aud mismatch. Expected {ClientId}, got {Aud}.", _options.ClientId, aud);
            return null;
        }

        // Require a verified email.
        if (!claims.TryGetValue("email_verified", out var emailVerified) ||
            !string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Google account email is not verified for token.");
            return null;
        }

        return claims;
    }

    private async Task<ApplicationUser?> FindOrCreateUserAsync(
        string email,
        Dictionary<string, string> claims,
        CancellationToken ct)
    {
        var existing = await _userManager.FindByEmailAsync(email);
        if (existing != null)
        {
            // If the email wasn't confirmed before, mark it confirmed now — Google verified it.
            if (!existing.EmailConfirmed)
            {
                existing.EmailConfirmed = true;
                await _userManager.UpdateAsync(existing);
            }
            return existing;
        }

        // Create a new user. Google has verified the email, so no confirmation needed.
        claims.TryGetValue("name", out var displayName);
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName ?? email,
            CreatedAt = DateTime.UtcNow
        };

        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Failed to create user for Google sign-in ({Email}): {Errors}", email, errors);
            return null;
        }

        _logger.LogInformation("Created new user {Email} (id {UserId}) via Google sign-in.", email, user.Id);
        return user;
    }

    /// <summary>
    /// Ensures a Google user is fully provisioned as the owner of their portfolio: an EMPTY
    /// <see cref="Portfolio"/> with the first-login Sandbox-vs-Live choice still pending, the
    /// <see cref="UserRole.Admin"/> Identity role, and a <see cref="UserAccount"/> staff row. Google
    /// sign-in (unlike email/password registration) did none of this, so the user had no portfolio
    /// (dashboard failed) and no role (only Dashboard + Help showed in the nav). Mirrors the seeded-admin
    /// pattern and <c>AuthService.ProvisionPendingPortfolioAsync</c>: no demo data is seeded here — the
    /// user picks Sandbox-vs-Live on first login. Every step is idempotent, so existing role-less users
    /// created before this fix are back-filled on their next login.
    /// </summary>
    private async Task EnsureOwnerProvisioningAsync(ApplicationUser user)
    {
        try
        {
            // 1) Empty portfolio with the choice pending — only if the user has none yet. No demo seed.
            if (user.PortfolioId == null)
            {
                var now = DateTime.UtcNow;
                var portfolio = new Portfolio
                {
                    Name = string.IsNullOrWhiteSpace(user.DisplayName) ? "My Portfolio" : $"{user.DisplayName}'s Portfolio",
                    ManagementCompanyName = string.IsNullOrWhiteSpace(user.DisplayName) ? "My Company" : user.DisplayName!,
                    Status = PortfolioStatus.Active,
                    Currency = "USD",
                    // Pending the first-login choice: not a sandbox yet, no demo data.
                    IsSandbox = false,
                    SandboxSeededAtUtc = null,
                    Settings = Domain.PortfolioOnboarding.WriteChoice(null, Domain.OnboardingChoice.Pending),
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.Portfolios.Add(portfolio);
                await _db.SaveChangesAsync();

                user.PortfolioId = portfolio.Id;
                await _userManager.UpdateAsync(user);

                _logger.LogInformation(
                    "Provisioned pending portfolio {PortfolioId} (awaiting Sandbox/Live choice) for Google user {Email} (id {UserId}).",
                    portfolio.Id, user.Email, user.Id);
            }

            // 2) Admin role — a self-service owner administers their own portfolio. Back-fills
            //    existing role-less Google users too.
            if (!await _userManager.IsInRoleAsync(user, nameof(UserRole.Admin)))
            {
                await _userManager.AddToRoleAsync(user, nameof(UserRole.Admin));
                _logger.LogInformation("Granted Admin role to Google user {Email} (id {UserId}).", user.Email, user.Id);
            }

            // 3) UserAccount staff row (mirrors the seeded admin / AdminUsersController.Create) so the
            //    owner appears under User Access and any UserAccount-scoped logic resolves them.
            if (user.PortfolioId is int pid &&
                !await _db.UserAccounts.AnyAsync(a => a.PortfolioId == pid && a.Email == user.Email))
            {
                var ts = DateTime.UtcNow;
                _db.UserAccounts.Add(new UserAccount
                {
                    PortfolioId = pid,
                    Email = user.Email!,
                    DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email! : user.DisplayName!,
                    PasswordHash = string.Empty, // Identity owns the credential
                    Role = UserRole.Admin,
                    IsActive = true,
                    CreatedAt = ts,
                    UpdatedAt = ts,
                });
                await _db.SaveChangesAsync();
            }

            // 4) Primary self-owner — the landlord IS the first owner. Idempotent, so this also
            //    back-fills Google users created before this step on their next login.
            if (user.PortfolioId is int ownerPid)
            {
                await _selfOwnerProvisioner.EnsureSelfOwnerAsync(user, ownerPid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed owner provisioning (portfolio/role/account) for Google user {Email} (id {UserId}); login still succeeds.",
                user.Email, user.Id);
        }
    }
}
