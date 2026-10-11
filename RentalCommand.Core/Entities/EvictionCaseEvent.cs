using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class EvictionCaseEvent : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int EvictionCaseId { get; set; }
    public EvictionEventType EventType { get; set; }
    public DateTime EventDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public EvictionCase? EvictionCase { get; set; }
}
