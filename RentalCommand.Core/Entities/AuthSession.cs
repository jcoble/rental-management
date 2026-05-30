namespace Lifecycle.Data.Entities;

public class AuthSession
{
    public int Id { get; set; }
    public int UserAccountId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public UserAccount? UserAccount { get; set; }
}
