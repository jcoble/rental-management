namespace RentalCommand.Core.Entities;

public class Notification : Interfaces.IAuditable, Interfaces.IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? UserId { get; set; }
    public string Type { get; set; } = "System";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public string? ActionUrl { get; set; }
    public string? RelatedEntityType { get; set; }
    public int? RelatedEntityId { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }

    public Portfolio? Portfolio { get; set; }
}
