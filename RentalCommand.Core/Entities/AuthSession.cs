using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Durable signed-in session. Refresh-token ownership moves here only when the new authentication
/// flow is activated; this kernel does not dual-write or reinterpret current refresh tokens.
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
}
