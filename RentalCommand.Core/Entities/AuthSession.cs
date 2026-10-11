using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Durable signed-in session. New refresh-token families are owned here, but the current auth flow
/// is deliberately not activated or dual-written by this kernel.
/// </summary>
public sealed class AuthSession
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public int ActiveAccessContextId { get; set; }
    public AuthSessionStatus Status { get; set; } = AuthSessionStatus.Active;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevocationReason { get; set; }

    public ApplicationUser? User { get; set; }
    public WorkspaceAccessContext? ActiveAccessContext { get; set; }
    public ICollection<AuthSessionRefreshTokenFamily> RefreshTokenFamilies { get; set; } =
        new List<AuthSessionRefreshTokenFamily>();
}
