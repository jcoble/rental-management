namespace RentalCommand.Core.Entities;

/// <summary>
/// Rotated, single-use refresh tokens for JWT auth. FK <see cref="UserId"/> is an
/// <c>int</c> matching <see cref="ApplicationUser"/>'s key.
/// </summary>
public class RefreshToken
{
    public int Id { get; set; }

    /// <summary>FK → <see cref="ApplicationUser.Id"/> (int).</summary>
    public int UserId { get; set; }

    public string Token { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime IssuedAt { get; set; }
    public bool IsRevoked { get; set; }
    public bool IsUsed { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public ApplicationUser? User { get; set; }
    public int? PortfolioId { get; set; }
}
