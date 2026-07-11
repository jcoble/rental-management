namespace RentalCommand.Core.Entities;

/// <summary>
/// Hashed, rotated single-use refresh credential. A consumed credential may identify exactly one
/// replacement in the same family; reuse and revocation are durable security facts.
/// </summary>
public sealed class AuthSessionRefreshCredential
{
    public Guid Id { get; set; }
    public Guid RefreshTokenFamilyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public Guid? ConsumedByOperationId { get; set; }
    public Guid? ReplacedByCredentialId { get; set; }
    public DateTime? ReuseDetectedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevocationReason { get; set; }

    public AuthSessionRefreshTokenFamily? RefreshTokenFamily { get; set; }
    public AuthSessionRefreshCredential? ReplacedByCredential { get; set; }
}
