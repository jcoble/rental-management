using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class ActivityLog
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public RentalActivityType Type { get; set; } = RentalActivityType.Note;
    public string? EntityType { get; set; }
    public int? EntityId { get; set; }
    public string? Action { get; set; }
    public string? Description { get; set; }
    public string? Actor { get; set; }
    public DateTime CreatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
}
