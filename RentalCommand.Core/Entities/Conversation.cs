namespace RentalCommand.Core.Entities;

/// <summary>
/// A topic-scoped message thread between the landlord and a single tenant (e.g. "Rent", "Maintenance").
/// A tenant can have multiple conversations, each carrying its own back-and-forth
/// <see cref="ConversationMessage"/> history. This is the canonical tenant inbox record.
/// </summary>
public class Conversation
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>The tenant this conversation is with.</summary>
    public int TenantId { get; set; }

    /// <summary>The conversation topic (e.g. "Rent", "Maintenance"). Max 200 chars.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Optional property this thread is about.</summary>
    public int? PropertyId { get; set; }

    /// <summary>
    /// Work assignment this thread belongs to, when it is an assignment conversation. A work order
    /// has at most one such thread; ordinary tenant topics keep this null.
    /// </summary>
    public int? WorkOrderId { get; set; }

    /// <summary>True when the landlord opened the thread; false when the tenant did.</summary>
    public bool StartedByLandlord { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Timestamp of the most recent message; the list sort key (desc).</summary>
    public DateTime LastMessageAt { get; set; }

    /// <summary>Truncated body of the most recent message for list previews. Max 280 chars.</summary>
    public string? LastMessagePreview { get; set; }

    /// <summary>Messages the landlord has not yet read (bumped on tenant sends, reset on landlord open).</summary>
    public int LandlordUnreadCount { get; set; }

    /// <summary>Messages the tenant has not yet read (bumped on landlord sends, reset on tenant open).</summary>
    public int TenantUnreadCount { get; set; }

    /// <summary>Messages the currently assigned maintenance technician has not read.</summary>
    public int TechnicianUnreadCount { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Tenant? Tenant { get; set; }
    public Property? Property { get; set; }
    public WorkOrder? WorkOrder { get; set; }
    public List<ConversationMessage> Messages { get; set; } = [];
}
