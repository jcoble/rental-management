using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// JWT-based authentication endpoints for the SvelteKit frontend. Access tokens are returned in the
/// response body; refresh tokens are set as an app-namespaced httpOnly, SameSite=Strict cookie and rotated on refresh.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IJwtTokenService _tokenService;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        IJwtTokenService tokenService,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _tokenService = tokenService;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// True only when running in Development AND the explicit, non-default opt-in flag
    /// <c>Auth:ExposeDevTokens</c> is set to <c>true</c>. Used to gate exposing email-confirmation
    /// and password-reset tokens in API responses for local dev convenience. Defaults to closed.
    /// </summary>
    private bool ShouldExposeDevTokens =>
        _environment.IsDevelopment() && _configuration.GetValue("Auth:ExposeDevTokens", false);

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        var result = await _authService.LoginAsync(request.Email, request.Password, GetIpAddress(), GetUserAgent());
        if (!result.Success)
        {
            return Unauthorized(new { error = result.Error ?? "Login failed" });
        }

        SetRefreshTokenCookie(result.Tokens!.RefreshToken, result.Tokens.RefreshTokenExpiration);
        return Ok(result.Response);
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var result = await _authService.RegisterAsync(request);
        if (!result.Success)
        {
            if (result.ErrorType == AuthErrorType.BadRequest)
            {
                return BadRequest(new
                {
                    error = result.Error ?? "Registration failed",
                    details = result.ValidationErrors
                });
            }
            return Unauthorized(new { error = result.Error ?? "Registration failed" });
        }

        const string genericMessage = "Registration successful. Please check your email to verify your account.";

        // Tokens are sensitive: only expose for local dev convenience under an explicit opt-in.
        if (ShouldExposeDevTokens)
        {
            _logger.LogWarning(
                "Auth:ExposeDevTokens is enabled: returning emailConfirmationToken for user {UserId} in the registration response. This must never be enabled outside local development.",
                result.UserId);

            return Ok(new
            {
                message = genericMessage,
                userId = result.UserId,
                emailConfirmationToken = result.EmailConfirmationToken
            });
        }

        return Ok(new { message = genericMessage });
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Refresh()
    {
        var refreshToken = Request.Cookies[AuthCookieNames.RefreshToken];
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Unauthorized(new { error = "Refresh token not found" });
        }

        var result = await _authService.RefreshAsync(refreshToken, GetIpAddress(), GetUserAgent());
        if (!result.Success)
        {
            ClearRefreshTokenCookies();
            return Unauthorized(new { error = result.Error ?? "Token refresh failed" });
        }

        SetRefreshTokenCookie(result.Tokens!.RefreshToken, result.Tokens.RefreshTokenExpiration);
        return Ok(result.Response);
    }

    [HttpPost("confirm-email")]
    [AllowAnonymous]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request)
    {
        var result = await _authService.ConfirmEmailAsync(request.UserId, request.Token);
        if (!result.Success)
        {
            if (result.ErrorType == AuthErrorType.NotFound)
            {
                return NotFound(new { error = result.Error ?? "User not found" });
            }
            return BadRequest(new { error = result.Error ?? "Email confirmation failed" });
        }

        return Ok(new { message = "Email confirmed successfully. You can now log in." });
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        const string genericMessage = "If an account exists with that email, a password reset link has been sent.";

        var token = await _authService.GeneratePasswordResetTokenAsync(request.Email);

        // The reset token must never be returned in the response by default (account-takeover risk).
        // Only expose it for local dev convenience under an explicit opt-in, and warn when doing so.
        if (ShouldExposeDevTokens)
        {
            _logger.LogWarning(
                "Auth:ExposeDevTokens is enabled: returning a password reset token in the forgot-password response. This must never be enabled outside local development.");

            return Ok(new
            {
                message = genericMessage,
                resetToken = token
            });
        }

        return Ok(new { message = genericMessage });
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var result = await _authService.ResetPasswordAsync(request.UserId, request.Token, request.NewPassword);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Error ?? "Password reset failed" });
        }

        return Ok(new { message = "Password has been reset successfully. You can now sign in." });
    }

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<UserDto>> GetCurrentUser()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { error = "Not authenticated" });
        }

        var result = await _authService.GetCurrentUserAsync(userId);
        if (!result.Success)
        {
            return NotFound(new { error = result.Error ?? "User not found" });
        }

        return Ok(result.User);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        var refreshToken = Request.Cookies[AuthCookieNames.RefreshToken];
        if (!string.IsNullOrEmpty(refreshToken))
        {
            await _tokenService.RevokeRefreshTokenAsync(refreshToken);
        }

        ClearRefreshTokenCookies();
        return Ok(new { message = "Logged out successfully" });
    }

    private void SetRefreshTokenCookie(string token, DateTime expiration)
    {
        Response.Cookies.Append(AuthCookieNames.RefreshToken, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = expiration
        });
        Response.Cookies.Delete(AuthCookieNames.LegacyRefreshToken);
    }

    private void ClearRefreshTokenCookies()
    {
        Response.Cookies.Delete(AuthCookieNames.RefreshToken);
        Response.Cookies.Delete(AuthCookieNames.LegacyRefreshToken);
    }

    private string? GetIpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? GetUserAgent() =>
        Request.Headers.TryGetValue("User-Agent", out var ua) ? ua.ToString() : null;
}
