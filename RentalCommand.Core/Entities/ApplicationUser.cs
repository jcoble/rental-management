using Microsoft.AspNetCore.Identity;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Application user for authentication. Uses an <c>int</c> primary key to match
/// all domain entities (intentional divergence from EdiPlatform's string/GUID key).
/// </summary>
public class ApplicationUser : IdentityUser<int>
{
    public int? PortfolioId { get; set; }
    public int? OwnerEntityId { get; set; }
    public int? TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public OwnerEntity? OwnerEntity { get; set; }
    public Tenant? Tenant { get; set; }
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
}
