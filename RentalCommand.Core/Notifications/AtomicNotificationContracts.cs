using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Notifications;

public sealed class AtomicMorningBriefingDigest
{
    public int PortfolioId { get; set; }
    public string PortfolioName { get; set; } = string.Empty;
    public int UserId { get; set; }
    public string LocalDate { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public bool EnableEmail { get; set; }
    public bool EnableSms { get; set; }
    public bool EnablePush { get; set; }
    public int ItemCount { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string DeviceTokensJson { get; set; } = "[]";
}

public sealed record AtomicNoticeApprovalCompletionValidation(
    bool IsMatch,
    int? RecipientConversationId);

public sealed class AtomicGeneratedTenantNoticeDraft
{
    public long? WorkItemId { get; init; }
    public int DraftId { get; init; }
    public bool WasCreated { get; init; }
    public int CreatedCount { get; init; }
    public int PortfolioId { get; init; }
    public int LeaseManagementId { get; init; }
    public int TenantAccountId { get; init; }
    public int RecipientLeaseManagementPartyId { get; init; }
    public int? LeaseAgreementId { get; init; }
    public int? LeaseAddendumId { get; init; }
    public long? TenantLedgerEntryId { get; init; }
    public int RecipientTenantId { get; init; }
    public int? PropertyId { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public string? PropertyName { get; init; }
    public string? UnitNumber { get; init; }
    public string NoticeType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTime TriggerDate { get; init; }
    public DateTime AppliedAtUtc { get; init; }
    public int? ConversationId { get; init; }
    public string? ApprovedChannels { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public DateTime? DismissedAt { get; init; }
}
