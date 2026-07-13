namespace RentalCommand.Api.DTOs;

public class NoticeDraftResponse
{
    public int Id { get; set; }
    public int LeaseManagementId { get; set; }
    public int TenantAccountId { get; set; }
    public long? TenantLedgerEntryId { get; set; }
    public int RecipientTenantId { get; set; }
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

/// <summary>
/// Optional scoping for notice generation. With no fields set, drafts are generated portfolio-wide for
/// every applicable relationship/open charge. Supplying <see cref="RecipientTenantId"/>,
/// <see cref="LeaseManagementId"/>, <see cref="TenantAccountId"/>, or
/// <see cref="TenantLedgerEntryId"/> narrows generation to that server-validated scope;
/// supplying <see cref="NoticeType"/> generates only that type and forces it for renewal/move-out even
/// when outside the usual trigger window (the landlord asked for it).
/// </summary>
public class GenerateNoticeDraftsRequest
{
    public int? RecipientTenantId { get; set; }
    public int? LeaseManagementId { get; set; }
    public int? TenantAccountId { get; set; }
    public long? TenantLedgerEntryId { get; set; }

    /// <summary>Canonical tenant-notice automation key; null = all applicable.</summary>
    public string? NoticeType { get; set; }
}

public class ApproveNoticeDraftRequest
{
    public List<string> Channels { get; set; } = ["Portal", "Email", "Sms"];

    /// <summary>
    /// Set true to send even when the Fair Housing review flags the notice copy. The landlord has
    /// reviewed the concerns and is consciously overriding the block (e.g. a false positive). The
    /// override is logged server-side. Default false → a flagged notice is blocked with a 422 carrying
    /// the concerns.
    /// </summary>
    public bool AcknowledgedFairHousingReview { get; set; }
}

public class UpdateNoticeDraftRequest
{
    public string? Subject { get; set; }
    public string? Body { get; set; }
}
