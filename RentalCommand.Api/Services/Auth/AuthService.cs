using Microsoft.AspNetCore.Identity;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Auth;

public enum AuthErrorType
{
    None,
    Unauthorized,
    BadRequest,
    NotFound
}

public class AuthResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public LoginResponse? Response { get; set; }
    public TokenResult? Tokens { get; set; }
    public IEnumerable<string>? ValidationErrors { get; set; }
    public AuthErrorType ErrorType { get; set; }

    /// <summary>
    /// Email-confirmation token surfaced to the controller. Since no email transport is wired in
    /// Phase 0, registration returns this so the confirm-email flow can be exercised end to end.
    /// </summary>
    public string? EmailConfirmationToken { get; set; }
    public int? UserId { get; set; }

    public static AuthResult Ok(LoginResponse response, TokenResult tokens) =>
        new() { Success = true, Response = response, Tokens = tokens };

    public static AuthResult Fail(string error, AuthErrorType errorType = AuthErrorType.Unauthorized) =>
        new() { Success = false, Error = error, ErrorType = errorType };

    public static AuthResult ValidationFail(string error, IEnumerable<string> details) =>
        new() { Success = false, Error = error, ValidationErrors = details, ErrorType = AuthErrorType.BadRequest };

    /// <summary>Registration succeeded but the user must confirm their email before logging in.</summary>
    public static AuthResult RegistrationPending(int userId, string emailConfirmationToken) =>
        new() { Success = true, UserId = userId, EmailConfirmationToken = emailConfirmationToken };
}

public class AuthUserResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public UserDto? User { get; set; }
    public AuthErrorType ErrorType { get; set; }

    public static AuthUserResult Ok(UserDto user) => new() { Success = true, User = user };

    public static AuthUserResult Fail(string error, AuthErrorType errorType = AuthErrorType.NotFound) =>
        new() { Success = false, Error = error, ErrorType = errorType };
}

public interface IAuthService
{
    Task<AuthResult> LoginAsync(string email, string password, string? ipAddress = null, string? userAgent = null);
    Task<AuthResult> RegisterAsync(RegisterRequest request);
    Task<AuthResult> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null);
    Task<AuthUserResult> ConfirmEmailAsync(string userId, string token);
    Task<AuthUserResult> GetCurrentUserAsync(string userId);

    /// <summary>
    /// Reset-password stub. Generates a reset token via Identity but does not send an email
    /// (no transport in Phase 0). Always succeeds to avoid email enumeration; returns the token
    /// for development convenience.
    /// </summary>
    Task<string?> GeneratePasswordResetTokenAsync(string email);
    Task<AuthUserResult> ResetPasswordAsync(string userId, string token, string newPassword);
    Task<UserDto> MapToUserDtoAsync(ApplicationUser user, IList<string> roles);
}

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IJwtTokenService _tokenService;
    private readonly IUserMigrationService _userMigration;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IJwtTokenService tokenService,
        IUserMigrationService userMigration,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _tokenService = tokenService;
        _userMigration = userMigration;
        _logger = logger;
    }

    public async Task<AuthResult> LoginAsync(string email, string password, string? ipAddress = null, string? userAgent = null)
    {
        var user = await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            return AuthResult.Fail("Invalid email or password");
        }

        // Rehash-on-first-login: a migrated account with no password hash can't sign in with a password.
        // Route it into the reset flow rather than returning a confusing "invalid password".
        if (await _userMigration.RequiresPasswordResetAsync(user))
        {
            _logger.LogInformation("Login for {Email} requires password reset (migrated account, no password set).", email);
            return AuthResult.Fail(
                "PASSWORD_RESET_REQUIRED: This account needs a password. Please use the reset-password flow.");
        }

        // lockoutOnFailure: true enables Identity's lockout (configured in Program.cs: 5 attempts / 5 min).
        var result = await _signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut)
            {
                return AuthResult.Fail("Account is locked. Try again later.");
            }
            return AuthResult.Fail("Invalid email or password");
        }

        // Email-confirm gate: block login until the address is verified.
        if (!user.EmailConfirmed)
        {
            return AuthResult.Fail("EMAIL_NOT_VERIFIED: Please verify your email address before logging in.");
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        var roles = await _userManager.GetRolesAsync(user);
        var tokens = await _tokenService.GenerateTokensAsync(user, roles, ipAddress, userAgent);

        return AuthResult.Ok(new LoginResponse
        {
            AccessToken = tokens.AccessToken,
            AccessTokenExpiration = tokens.AccessTokenExpiration,
            User = await MapToUserDtoAsync(user, roles)
        }, tokens);
    }

    public async Task<AuthResult> RegisterAsync(RegisterRequest request)
    {
        var existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser != null)
        {
            return AuthResult.Fail("Email is already registered", AuthErrorType.BadRequest);
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = false, // email-confirm gate
            DisplayName = request.DisplayName,
            CreatedAt = DateTime.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            return AuthResult.ValidationFail(
                "Registration failed",
                createResult.Errors.Select(e => e.Description));
        }

        var emailToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);

        _logger.LogInformation("New user registered: {Email} (id {UserId}). Awaiting email verification.", request.Email, user.Id);

        return AuthResult.RegistrationPending(user.Id, emailToken);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null)
    {
        var tokens = await _tokenService.RefreshTokenAsync(refreshToken, ipAddress, userAgent);
        if (tokens == null)
        {
            return AuthResult.Fail("Invalid or expired refresh token");
        }

        var principal = _tokenService.ValidateAccessToken(tokens.AccessToken);
        var userId = principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var user = userId != null ? await _userManager.FindByIdAsync(userId) : null;

        if (user == null)
        {
            return AuthResult.Fail("User not found");
        }

        if (!user.EmailConfirmed)
        {
            return AuthResult.Fail("EMAIL_NOT_VERIFIED: Please verify your email address before logging in.");
        }

        var roles = await _userManager.GetRolesAsync(user);

        return AuthResult.Ok(new LoginResponse
        {
            AccessToken = tokens.AccessToken,
            AccessTokenExpiration = tokens.AccessTokenExpiration,
            User = await MapToUserDtoAsync(user, roles)
        }, tokens);
    }

    public async Task<AuthUserResult> ConfirmEmailAsync(string userId, string token)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return AuthUserResult.Fail("User not found");
        }

        var result = await _userManager.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            return AuthUserResult.Fail($"Email confirmation failed: {errors}", AuthErrorType.BadRequest);
        }

        var roles = await _userManager.GetRolesAsync(user);
        return AuthUserResult.Ok(await MapToUserDtoAsync(user, roles));
    }

    public async Task<AuthUserResult> GetCurrentUserAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return AuthUserResult.Fail("User not found");
        }

        var roles = await _userManager.GetRolesAsync(user);
        return AuthUserResult.Ok(await MapToUserDtoAsync(user, roles));
    }

    public async Task<string?> GeneratePasswordResetTokenAsync(string email)
    {
        var user = await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            // Do not reveal whether the email exists.
            return null;
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        // Phase 0: no email transport. A later phase sends this via INotificationChannel.
        _logger.LogInformation("Password reset token generated for {Email} (id {UserId}).", email, user.Id);
        return token;
    }

    public async Task<AuthUserResult> ResetPasswordAsync(string userId, string token, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return AuthUserResult.Fail("Invalid or expired reset link.", AuthErrorType.BadRequest);
        }

        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            return AuthUserResult.Fail(errors, AuthErrorType.BadRequest);
        }

        // Confirm the email too in case they reset before verifying.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await _userManager.UpdateAsync(user);
        }

        var roles = await _userManager.GetRolesAsync(user);
        return AuthUserResult.Ok(await MapToUserDtoAsync(user, roles));
    }

    public Task<UserDto> MapToUserDtoAsync(ApplicationUser user, IList<string> roles)
    {
        return Task.FromResult(new UserDto
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName ?? user.Email ?? string.Empty,
            PortfolioId = user.PortfolioId,
            OwnerEntityId = user.OwnerEntityId,
            TenantId = user.TenantId,
            Roles = roles.ToList(),
            EmailVerified = user.EmailConfirmed
        });
    }
}
