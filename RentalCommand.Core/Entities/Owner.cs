namespace RentalCommand.Core.Entities;

public class Owner
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? MailingAddress { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public List<Property> Properties { get; set; } = [];
    public List<UserAccount> UserAccounts { get; set; } = [];
}
