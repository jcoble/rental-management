namespace RentalCommand.Core.Entities;

/// <summary>
/// Short-lived proof that primary authentication succeeded but the user still has to select one
/// of several effective workspace contexts. Only a hash of the opaque challenge is persisted.
/// Creating a challenge never creates an authenticated session.
/// </summary>
public sealed class LoginContextSelectionChallenge
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }

    public ApplicationUser? User { get; set; }
}
