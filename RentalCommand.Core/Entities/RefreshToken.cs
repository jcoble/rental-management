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

    /// <summary>
    /// Reuse grace deadline, set atomically the moment this token is rotated (marked used). A legitimate
    /// session has several independent refresh triggers (SSR /auth/me 401, client 401-retry, proactive
    /// refresh, SignalR accessTokenFactory, and the mobile Dio interceptor — a SEPARATE process from the
    /// web node) that can each present the same refresh token in close succession. Within this window a
    /// re-presentation of the just-rotated token is treated as a benign straggler and re-served a fresh
    /// pair instead of tripping the family-revoke. Persisting the deadline on the row (rather than an
    /// in-process map) makes the grace correct across multiple API instances. Null until the token is
    /// rotated; a re-presentation after the deadline still hits the hard family-revoke (theft protection).
    /// </summary>
    public DateTime? GraceExpiresAt { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public ApplicationUser? User { get; set; }
    public int? PortfolioId { get; set; }
}
