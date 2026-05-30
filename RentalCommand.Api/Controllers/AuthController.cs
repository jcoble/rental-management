using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// JWT-based authentication endpoints for the SvelteKit frontend. Access tokens are returned in the
/// response body; refresh tokens are set as an httpOnly, SameSite=Strict cookie and rotated on refresh.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private const string RefreshCookieName = "refresh_token";

    private readonly IAuthService _authService;
    private readonly IJwtTokenService _tokenService;

    public AuthController(IAuthService authService, IJwtTokenService tokenService)
    {
        _authService = authService;
        _tokenService = tokenService;
    }

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

        // Phase 0 has no email transport: return the confirmation token so the flow is exercisable.
        return Ok(new
        {
            message = "Registration successful. Please check your email to verify your account.",
            userId = result.UserId,
            emailConfirmationToken = result.EmailConfirmationToken
        });
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Refresh()
    {
        var refreshToken = Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Unauthorized(new { error = "Refresh token not found" });
        }

        var result = await _authService.RefreshAsync(refreshToken, GetIpAddress(), GetUserAgent());
        if (!result.Success)
        {
            Response.Cookies.Delete(RefreshCookieName);
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
        // Reset-password stub: no email transport in Phase 0. Token is returned for dev convenience.
        var token = await _authService.GeneratePasswordResetTokenAsync(request.Email);
        return Ok(new
        {
            message = "If an account exists with that email, a password reset link has been sent.",
            resetToken = token
        });
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
        var refreshToken = Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrEmpty(refreshToken))
        {
            await _tokenService.RevokeRefreshTokenAsync(refreshToken);
        }

        Response.Cookies.Delete(RefreshCookieName);
        return Ok(new { message = "Logged out successfully" });
    }

    private void SetRefreshTokenCookie(string token, DateTime expiration)
    {
        Response.Cookies.Append(RefreshCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = expiration
        });
    }

    private string? GetIpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? GetUserAgent() =>
        Request.Headers.TryGetValue("User-Agent", out var ua) ? ua.ToString() : null;
}
