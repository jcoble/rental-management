namespace RentalCommand.Api.DTOs;

public class NoticeDraftResponse
{
    public int Id { get; set; }
    public int LeaseId { get; set; }
    public int TenantId { get; set; }
    public int? PropertyId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string? PropertyName { get; set; }
    public string? UnitNumber { get; set; }
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
}

public class GenerateNoticeDraftsResponse
{
    public int CreatedCount { get; set; }
    public IReadOnlyList<NoticeDraftResponse> Drafts { get; set; } = [];
}

public class ApproveNoticeDraftRequest
{
    public List<string> Channels { get; set; } = ["Portal", "Email", "Sms"];
}

public class UpdateNoticeDraftRequest
{
    public string? Subject { get; set; }
    public string? Body { get; set; }
}
