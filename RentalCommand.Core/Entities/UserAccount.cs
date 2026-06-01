using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class UserAccount
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? OwnerId { get; set; }
    public int? TenantId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Tenant;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Owner? Owner { get; set; }
    public Tenant? Tenant { get; set; }
    public List<PortalMessage> Messages { get; set; } = [];
}
