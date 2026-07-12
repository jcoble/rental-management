using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Auth;

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
    private readonly IGoogleAuthService _googleAuthService;
    private readonly GoogleAuthOptions _googleOptions;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly IAtomicAuthSessionCredentialService _atomicCredentials;
    private readonly ICanonicalAccessTokenService _canonicalTokens;
    private readonly IAccessEnvelopeQuery _accessEnvelopes;
    private readonly IEffectiveAccessContextSelectionQuery _contextSelection;
    private readonly TimeProvider _timeProvider;

    public AuthController(
        IAuthService authService,
        IJwtTokenService tokenService,
        IGoogleAuthService googleAuthService,
        IOptions<GoogleAuthOptions> googleOptions,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IAtomicAuthSessionCredentialService atomicCredentials,
        ICanonicalAccessTokenService canonicalTokens,
        IAccessEnvelopeQuery accessEnvelopes,
        IEffectiveAccessContextSelectionQuery contextSelection,
        TimeProvider timeProvider,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _tokenService = tokenService;
        _googleAuthService = googleAuthService;
        _googleOptions = googleOptions.Value;
        _environment = environment;
        _configuration = configuration;
        _atomicCredentials = atomicCredentials;
        _canonicalTokens = canonicalTokens;
        _accessEnvelopes = accessEnvelopes;
        _contextSelection = contextSelection;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// True only when running in Development AND the explicit, non-default opt-in flag
    /// <c>Auth:ExposeDevTokens</c> is set to <c>true</c>. Used to gate exposing email-confirmation
    /// and password-reset tokens in API responses for local dev convenience. Defaults to closed.
    /// </summary>
    private bool ShouldExposeDevTokens =>
        _environment.IsDevelopment() && _configuration.GetValue("Auth:ExposeDevTokens", false);

    /// <summary>
    /// Request header a non-cookie (mobile) client sets to opt into receiving the refresh token in the
    /// response body. The httpOnly refresh cookie is always set as well; only callers that send this
    /// header (value <c>mobile</c>) additionally get <see cref="LoginResponse.RefreshToken"/> populated.
    /// Browsers never send it, so web responses keep the token out of JS-readable bodies.
    /// </summary>
    private const string ClientTypeHeader = "X-Client-Type";
    private const string MobileClientType = "mobile";

    /// <summary>
    /// True when the caller identifies itself as the mobile client via <see cref="ClientTypeHeader"/>.
    /// Mobile has no httpOnly-cookie jar it can read, so it needs the rotated refresh token in the body.
    /// </summary>
    private bool IsMobileClient =>
        Request.Headers.TryGetValue(ClientTypeHeader, out var clientType) &&
        string.Equals(clientType.ToString(), MobileClientType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies the rotated refresh token into the response body, but only for mobile callers. Web callers
    /// get <c>null</c> (serialized out), so the token stays confined to the httpOnly cookie for browsers.
    /// </summary>
    private void PopulateBodyRefreshTokenForMobile(LoginResponse? response, TokenResult tokens)
    {
        if (response is not null && IsMobileClient)
        {
            response.RefreshToken = tokens.RefreshToken;
        }
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        var result = await _authService.LoginAsync(
            request.Email,
            request.Password,
            request.AccessContextId,
            GetIpAddress(),
            GetUserAgent());
        if (!result.Success)
        {
            return Unauthorized(new { error = result.Error ?? "Login failed" });
        }

        SetRefreshTokenCookie(result.Tokens!.RefreshToken, result.Tokens.RefreshTokenExpiration);
        PopulateBodyRefreshTokenForMobile(result.Response, result.Tokens);
        return Ok(result.Response);
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        await Task.CompletedTask;
        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new
            {
                error = "Registration is unavailable until workspace membership provisioning uses the canonical atomic authority command.",
            });
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Refresh([FromBody] RefreshRequest? request = null)
    {
        // Prefer an explicit body token (mobile clients send it directly, avoiding any
        // cookie crafting); fall back to the rotated refresh cookie (web clients).
        var refreshToken = request?.RefreshToken ?? Request.Cookies[AuthCookieNames.RefreshToken];
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
        PopulateBodyRefreshTokenForMobile(result.Response, result.Tokens);
        return Ok(result.Response);
    }

    [HttpGet("access")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> GetAccess(CancellationToken ct)
    {
        if (!TryGetActiveAccessContext(out var active))
        {
            return Unauthorized(new { error = "Active access context is unavailable." });
        }

        var envelope = await _accessEnvelopes.GetAsync(active.UserId, active.AccessContextId, ct);
        return envelope is null
            ? Unauthorized(new { error = "Active access context is unavailable." })
            : Ok(envelope);
    }

    [HttpGet("contexts")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> ListAccessContexts(CancellationToken ct)
    {
        if (!TryGetActiveAccessContext(out var active))
        {
            return Unauthorized(new { error = "Active access context is unavailable." });
        }

        return Ok(await _contextSelection.ListAsync(
            active.UserId,
            _timeProvider.GetUtcNow().UtcDateTime,
            ct));
    }

    [HttpPost("contexts/select")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<SwitchAccessContextResponse>> SelectAccessContext(
        [FromBody] SwitchAccessContextRequest request,
        CancellationToken ct)
    {
        if (!TryGetActiveAccessContext(out var active))
        {
            return Unauthorized(new { error = "Active access context is unavailable." });
        }

        try
        {
            var switched = await _atomicCredentials.SwitchContextAsync(
                new SwitchAuthSessionContextCommand(
                    active.SessionId,
                    active.UserId,
                    active.AccessContextId,
                    active.AccessRevision,
                    request.AccessContextId,
                    _timeProvider.GetUtcNow().UtcDateTime),
                Guid.NewGuid(),
                ct);
            var envelope = await _accessEnvelopes.GetAsync(
                switched.UserId,
                switched.AccessContextId,
                ct);
            if (!switched.Switched || envelope is null)
            {
                return Forbid();
            }

            var access = _canonicalTokens.Issue(new CanonicalAccessCoordinates(
                switched.UserId,
                switched.AuthSessionId,
                switched.AccessContextId,
                switched.AccessRevision));
            return Ok(new SwitchAccessContextResponse
            {
                AccessToken = access.Token,
                AccessTokenExpiration = access.ExpiresAtUtc,
                Access = envelope,
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private bool TryGetActiveAccessContext(out ActiveAccessContext active)
    {
        if (HttpContext.Items.TryGetValue(
                CanonicalAccessContextHttpItem.Key,
                out var value) &&
            value is ActiveAccessContext context)
        {
            active = context;
            return true;
        }

        active = null!;
        return false;
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

    [HttpPost("resend-verification")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationRequest request)
    {
        var result = await _authService.ResendVerificationEmailAsync(request.Email);

        // Neutral response either way (no account enumeration). When the account is already
        // verified we can say so — that is not an enumeration signal a logged-out attacker can
        // act on, and it helps a real user who simply forgot they had already confirmed.
        if (result.Error is not null && result.Error.Contains("already verified", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { message = "Email is already verified. You can log in." });
        }

        return Ok(new { message = "If an account exists, a verification email has been sent." });
    }

    [HttpPost("change-password")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { error = "Not authenticated" });
        }

        var result = await _authService.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword);
        if (!result.Success)
        {
            if (result.ErrorType == AuthErrorType.NotFound)
            {
                return NotFound(new { error = result.Error ?? "User not found" });
            }
            return BadRequest(new { error = result.Error ?? "Password change failed" });
        }

        return Ok(new { message = "Password changed successfully." });
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
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> Logout()
    {
        if (TryGetActiveAccessContext(out var active))
        {
            await _atomicCredentials.RevokeSessionAsync(
                new RevokeAuthSessionCommand(
                    active.SessionId,
                    active.UserId,
                    active.AccessContextId,
                    active.AccessRevision,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    "User signed out"),
                Guid.NewGuid(),
                HttpContext.RequestAborted);
        }

        ClearRefreshTokenCookies();
        return Ok(new { message = "Logged out successfully" });
    }

    /// <summary>
    /// Sign in with Google. Web clients send an authorization <c>code</c> + <c>redirectUri</c> (the
    /// server exchanges it with Google); native (mobile) clients send a Google <c>idToken</c>
    /// obtained on-device (the server validates it directly). Either way the local user is
    /// found/created and issued our own JWT pair. Returns 501 when Google is not configured.
    /// </summary>
    [HttpPost("google")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> GoogleSignIn([FromBody] GoogleAuthRequest request)
    {
        await Task.CompletedTask;
        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new { error = "Google sign-in is unavailable until it issues canonical workspace sessions." });
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
