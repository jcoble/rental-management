using Microsoft.AspNetCore.Identity;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Application user for authentication. Uses an <c>int</c> primary key to match
/// all domain entities (intentional divergence from EdiPlatform's string/GUID key).
/// </summary>
public class ApplicationUser : IdentityUser<int>
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
    public ICollection<WorkspaceAccessContext> WorkspaceAccessContexts { get; set; } =
        new List<WorkspaceAccessContext>();
    public ICollection<OwnerUserAccess> OwnerUserAccesses { get; set; } = new List<OwnerUserAccess>();
    public ICollection<TenantUserAccess> TenantUserAccesses { get; set; } = new List<TenantUserAccess>();
    public ICollection<AuthSession> AuthSessions { get; set; } = new List<AuthSession>();
    public ICollection<LoginContextSelectionChallenge> LoginContextSelectionChallenges { get; set; } =
        new List<LoginContextSelectionChallenge>();
}
