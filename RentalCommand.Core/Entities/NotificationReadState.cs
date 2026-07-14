namespace RentalCommand.Core.Entities;

/// <summary>
/// Records that one user has read one notification. Broadcast notifications remain immutable and
/// gain an independent row per reader, so one workspace member cannot change another member's
/// unread state.
/// </summary>
public sealed class NotificationReadState : Interfaces.IPortfolioScoped
{
    public int PortfolioId { get; set; }
    public int NotificationId { get; set; }
    public int UserId { get; set; }
    public DateTime ReadAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Notification? Notification { get; set; }
    public ApplicationUser? User { get; set; }
}
