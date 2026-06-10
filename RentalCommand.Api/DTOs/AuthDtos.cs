using System.ComponentModel.DataAnnotations;

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

public class LoginResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTime AccessTokenExpiration { get; set; }
    public UserDto User { get; set; } = new();
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
