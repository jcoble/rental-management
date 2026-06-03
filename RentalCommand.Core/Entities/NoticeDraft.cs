namespace RentalCommand.Core.Entities;

public class NoticeDraft
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public int TenantId { get; set; }
    public int? PropertyId { get; set; }
    public string NoticeType { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime TriggerDate { get; set; }
    public int? ConversationId { get; set; }
    public string? ApprovedChannels { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? DismissedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Lease? Lease { get; set; }
    public Tenant? Tenant { get; set; }
    public Property? Property { get; set; }
    public Conversation? Conversation { get; set; }
}
