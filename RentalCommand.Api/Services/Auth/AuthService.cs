using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

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

    /// <summary>
    /// Re-sends the email-confirmation message for an unverified account. Always reports success
    /// to the caller (no account enumeration): a missing email or an already-confirmed account is
    /// a silent no-op. Only an existing, still-unconfirmed account actually generates a fresh token
    /// and enqueues the email.
    /// </summary>
    Task<AuthUserResult> ResendVerificationEmailAsync(string email);

    /// <summary>
    /// Changes the signed-in user's password. Rejects accounts with no local password (external
    /// login only, e.g. Google) and surfaces Identity's password-policy/validation failures.
    /// </summary>
    Task<AuthUserResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword);

    Task<UserDto> MapToUserDtoAsync(ApplicationUser user, IList<string> roles);
}

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IJwtTokenService _tokenService;
    private readonly IUserMigrationService _userMigration;
    private readonly IAuthEmailSender _emailSender;
    private readonly RentalCommandDbContext _db;
    private readonly Domain.ISelfOwnerProvisioner _selfOwnerProvisioner;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IJwtTokenService tokenService,
        IUserMigrationService userMigration,
        IAuthEmailSender emailSender,
        RentalCommandDbContext db,
        Domain.ISelfOwnerProvisioner selfOwnerProvisioner,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _tokenService = tokenService;
        _userMigration = userMigration;
        _emailSender = emailSender;
        _db = db;
        _selfOwnerProvisioner = selfOwnerProvisioner;
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

        // New signups get an EMPTY portfolio with the first-login Sandbox-vs-Live choice still PENDING.
        // We deliberately do NOT auto-seed demo data here: the user is asked, on first login, whether to
        // "Explore with sample data (Sandbox)" or "Set up my real portfolio (Live)", and the demo seed
        // runs only if they pick Sandbox (POST /api/v1/portfolio/onboarding-choice). Resilient: a
        // provisioning failure must NOT fail registration — the account is still created and usable.
        await ProvisionPendingPortfolioAsync(user);

        var emailToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);

        await _emailSender.SendEmailConfirmationAsync(user, emailToken);

        _logger.LogInformation("New user registered: {Email} (id {UserId}). Awaiting email verification.", request.Email, user.Id);

        return AuthResult.RegistrationPending(user.Id, emailToken);
    }

    /// <summary>
    /// Creates a fresh EMPTY <see cref="Portfolio"/> for a just-registered user with the first-login
    /// Sandbox-vs-Live choice still PENDING, scopes the user to it, and provisions the self-owner + Admin
    /// role + staff row. It deliberately does NOT seed demo data and does NOT set <c>IsSandbox</c>: the
    /// account stays a blank Live-shaped portfolio until the user makes the first-login choice. Picking
    /// "Sandbox" later seeds the demo data and flips the flag (POST /portfolio/onboarding-choice). Picking
    /// "Live" keeps it empty. Best-effort and self-contained: any failure is logged and swallowed so it can
    /// never fail the registration that already succeeded.
    /// </summary>
    private async Task ProvisionPendingPortfolioAsync(ApplicationUser user)
    {
        try
        {
            var now = DateTime.UtcNow;
            var portfolio = new Portfolio
            {
                Name = string.IsNullOrWhiteSpace(user.DisplayName) ? "My Portfolio" : $"{user.DisplayName}'s Portfolio",
                ManagementCompanyName = string.IsNullOrWhiteSpace(user.DisplayName) ? "My Company" : user.DisplayName!,
                Status = PortfolioStatus.Active,
                Currency = "USD",
                // Pending the first-login choice: not a sandbox yet, no demo data. Settings carries the
                // pending marker so the landing logic routes the user to the choice gate.
                IsSandbox = false,
                SandboxSeededAtUtc = null,
                Settings = Domain.PortfolioOnboarding.WriteChoice(null, Domain.OnboardingChoice.Pending),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Portfolios.Add(portfolio);
            await _db.SaveChangesAsync();

            // Scope the new user to their portfolio so their first JWT carries this portfolioId.
            user.PortfolioId = portfolio.Id;
            await _userManager.UpdateAsync(user);

            // The landlord IS the first owner: auto-create a primary self-owner from their account and
            // link it, so onboarding never needs a separate "add an owner" step. Idempotent; needed for
            // both the Sandbox and Live paths (the Go-Live wipe later recreates it).
            await _selfOwnerProvisioner.EnsureSelfOwnerAsync(user, portfolio.Id);

            // A self-service owner administers their own portfolio: grant the Admin role + a UserAccount
            // staff row (mirrors the seeded admin). Without a role the nav only shows Dashboard + Help.
            if (!await _userManager.IsInRoleAsync(user, nameof(UserRole.Admin)))
            {
                await _userManager.AddToRoleAsync(user, nameof(UserRole.Admin));
            }
            if (!await _db.UserAccounts.AnyAsync(a => a.PortfolioId == portfolio.Id && a.Email == user.Email))
            {
                _db.UserAccounts.Add(new UserAccount
                {
                    PortfolioId = portfolio.Id,
                    Email = user.Email!,
                    DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email! : user.DisplayName!,
                    PasswordHash = string.Empty, // Identity owns the credential
                    Role = UserRole.Admin,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await _db.SaveChangesAsync();
            }

            _logger.LogInformation(
                "Provisioned pending portfolio {PortfolioId} (Admin role + account, awaiting Sandbox/Live choice) for new user {Email} (id {UserId}).",
                portfolio.Id, user.Email, user.Id);
        }
        catch (Exception ex)
        {
            // Never fail registration over portfolio provisioning — log and continue.
            _logger.LogError(ex,
                "Failed to provision pending portfolio for new user {Email} (id {UserId}); registration still succeeds.",
                user.Email, user.Id);
        }
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
        await _emailSender.SendPasswordResetAsync(user, token);
        _logger.LogInformation("Password reset token generated for {Email} (id {UserId}). Reset email enqueued.", email, user.Id);
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

    public async Task<AuthUserResult> ResendVerificationEmailAsync(string email)
    {
        var user = await _userManager.FindByEmailAsync(email);

        // Do not reveal whether the email exists, or whether it is already confirmed, to the
        // caller in a way that distinguishes accounts. Both branches return success; only an
        // existing, still-unconfirmed account actually sends an email.
        if (user == null)
        {
            _logger.LogInformation("Resend-verification requested for an unknown email; no-op.");
            return AuthUserResult.Ok(null!);
        }

        if (user.EmailConfirmed)
        {
            // Carry an "already verified" marker so the controller can optionally tailor copy,
            // without leaking it as an enumeration signal (the controller still returns neutral text).
            return new AuthUserResult { Success = true, Error = "Email is already verified" };
        }

        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        await _emailSender.SendEmailConfirmationAsync(user, token);
        _logger.LogInformation("Verification email resent for {Email} (id {UserId}).", email, user.Id);
        return AuthUserResult.Ok(null!);
    }

    public async Task<AuthUserResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return AuthUserResult.Fail("User not found");
        }

        // An external-login-only account (e.g. Google) has no local password to change. Reject
        // clearly rather than letting Identity emit a confusing "incorrect password" error.
        if (!await _userManager.HasPasswordAsync(user))
        {
            return AuthUserResult.Fail(
                "This account signs in with Google and has no password to change.",
                AuthErrorType.BadRequest);
        }

        var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            return AuthUserResult.Fail(errors, AuthErrorType.BadRequest);
        }

        _logger.LogInformation("Password changed for user {UserId}.", userId);
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
