using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class PortalMessage
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int UserAccountId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public PortalMessageStatus Status { get; set; } = PortalMessageStatus.Open;
    public string? Reply { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public UserAccount? UserAccount { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
}
