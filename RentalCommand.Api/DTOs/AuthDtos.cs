using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.DTOs;

public class LoginRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Required only when this identity has more than one effective workspace context. The API
    /// never guesses a workspace from a legacy user column.
    /// </summary>
    public int? AccessContextId { get; set; }
}

public class RegisterRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(200)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Optional body for <c>POST /auth/refresh</c>. Non-cookie clients (the Flutter mobile
/// app) send the stored refresh token here instead of relying on a cookie. Web clients
/// post no body and continue to rely on the <c>rc_refresh_token</c> cookie, so this is
/// nullable and the controller falls back to the cookie when it is absent.
/// </summary>
public class RefreshRequest
{
    [MaxLength(4000)]
    public string? RefreshToken { get; set; }
}

public sealed class SwitchAccessContextRequest
{
    [Range(1, int.MaxValue)]
    public int AccessContextId { get; set; }
}

public sealed class SwitchAccessContextResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTime AccessTokenExpiration { get; set; }
    public AccessEnvelope Access { get; set; } = null!;
}

public class LoginResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTime AccessTokenExpiration { get; set; }
    public UserDto User { get; set; } = new();
    public AccessEnvelope Access { get; set; } = null!;

    /// <summary>
    /// The refresh token, in the response body. Populated ONLY for non-cookie (mobile) callers that
    /// opt in via the <c>X-Client-Type: mobile</c> header — the mobile app stores it in the OS secure
    /// enclave and sends it back in the refresh body. Web clients never receive it here (it stays
    /// <c>null</c> and is omitted from the JSON): they continue to rely on the httpOnly
    /// <c>rc_refresh_token</c> cookie, so the token is never exposed to browser JS.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; set; }
}

public class ConfirmEmailRequest
{
    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Token { get; set; } = string.Empty;
}

public class ForgotPasswordRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordRequest
{
    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Token { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(200)]
    public string NewPassword { get; set; } = string.Empty;
}

public class ResendVerificationRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    [Required]
    [MaxLength(200)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(200)]
    public string NewPassword { get; set; } = string.Empty;
}

public class GoogleAuthRequest
{
    /// <summary>Web OAuth flow: the authorization code to exchange server-side (with <see cref="RedirectUri"/>).</summary>
    [MaxLength(4000)]
    public string? Code { get; set; }

    /// <summary>Web OAuth flow: the redirect URI the <see cref="Code"/> was issued for.</summary>
    [MaxLength(2000)]
    public string? RedirectUri { get; set; }

    /// <summary>
    /// Native (mobile) flow: a Google id_token obtained on-device via <c>google_sign_in</c>. When
    /// present, the server validates it directly and skips the authorization-code exchange.
    /// </summary>
    [MaxLength(4000)]
    public string? IdToken { get; set; }
}

public class UserDto
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? PortfolioId { get; set; }
    public int? OwnerEntityId { get; set; }
    public int? TenantId { get; set; }
    public List<string> Roles { get; set; } = new();
    public bool EmailVerified { get; set; }
}
