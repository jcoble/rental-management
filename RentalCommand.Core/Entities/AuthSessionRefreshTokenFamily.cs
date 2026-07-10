namespace RentalCommand.Core.Entities;

/// <summary>
/// Session-owned refresh-token family for the future authentication cutover. Reuse revokes the
/// entire family. This model has no relationship to the legacy user-owned RefreshToken table.
/// </summary>
public sealed class AuthSessionRefreshTokenFamily
{
    public Guid Id { get; set; }
    public Guid AuthSessionId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime AbsoluteExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevocationReason { get; set; }
    public DateTime? ReuseDetectedAtUtc { get; set; }

    public AuthSession? AuthSession { get; set; }
    public ICollection<AuthSessionRefreshCredential> Credentials { get; set; } =
        new List<AuthSessionRefreshCredential>();
}
