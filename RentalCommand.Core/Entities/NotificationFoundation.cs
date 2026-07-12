using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>Personal alert destinations for exactly one signed-in user.</summary>
public sealed class UserAlertPreference
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int UserId { get; set; }
    public bool EnableInApp { get; set; } = true;
    public bool EnableMobilePush { get; set; } = true;
    public bool EnableEmail { get; set; } = true;
    public bool EnableSms { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Portfolio? Portfolio { get; set; }
    public ApplicationUser? User { get; set; }
}

/// <summary>Administrative rule for resolving internal recipients for one topic.</summary>
public sealed class TeamRoutingRule
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public TeamRoutingTopic Topic { get; set; }
    public int? PropertyId { get; set; }
    public bool UseWorkspaceAdministratorFallback { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public ICollection<TeamRoutingRuleRecipient> Recipients { get; set; } = [];
}

public sealed class TeamRoutingRuleRecipient
{
    public int Id { get; set; }
    public int TeamRoutingRuleId { get; set; }
    public int PortfolioId { get; set; }
    public int UserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public TeamRoutingRule? Rule { get; set; }
    public ApplicationUser? User { get; set; }
}

/// <summary>Independent policy for one tenant-facing automation. There is intentionally no master switch.</summary>
public sealed class TenantNoticePolicy
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string AutomationKey { get; set; } = string.Empty;
    public TenantNoticeMode Mode { get; set; } = TenantNoticeMode.Draft;
    public NoticeClassification Classification { get; set; }
    public int LeadDays { get; set; }
    public int SendHourLocal { get; set; } = 9;
    public bool SendTenantPortal { get; set; } = true;
    public bool SendMobilePush { get; set; }
    public bool SendEmail { get; set; } = true;
    public bool SendSms { get; set; }
    public bool IncludePrimaryTenant { get; set; } = true;
    public bool IncludeCoTenant { get; set; } = true;
    public bool IncludeEligibleGuarantor { get; set; }
    public bool IncludeOccupant { get; set; }
    public NoticeFailureBehavior FailureBehavior { get; set; } = NoticeFailureBehavior.StopAndRequireReview;
    public int WorkspaceNoticeTemplateVersionId { get; set; }
    public string? ReviewedJurisdictionCode { get; set; }
    public DateTime? JurisdictionReviewedAtUtc { get; set; }
    public int? JurisdictionReviewedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Portfolio? Portfolio { get; set; }
    public WorkspaceNoticeTemplateVersion? TemplateVersion { get; set; }

    public bool CanAutoSend => Mode == TenantNoticeMode.Auto &&
        (Classification != NoticeClassification.Legal ||
         (!string.IsNullOrWhiteSpace(ReviewedJurisdictionCode) && JurisdictionReviewedAtUtc != null));
}

/// <summary>Immutable platform-supplied notice copy.</summary>
public sealed class SystemNoticeTemplateVersion
{
    public int Id { get; set; }
    public string SystemKey { get; set; } = string.Empty;
    public int Version { get; set; }
    public NoticeClassification Classification { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? JurisdictionCode { get; set; }
    public string Provenance { get; set; } = string.Empty;
    public DateTime PublishedAtUtc { get; set; }
}

/// <summary>Append-only workspace template version; edits and restores always create a successor.</summary>
public sealed class WorkspaceNoticeTemplateVersion
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string SystemKey { get; set; } = string.Empty;
    public int Version { get; set; }
    public int BasedOnSystemTemplateVersionId { get; set; }
    public bool IsCustomized { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? JurisdictionCode { get; set; }
    public DateTime? JurisdictionReviewedAtUtc { get; set; }
    public int? JurisdictionReviewedByUserId { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public SystemNoticeTemplateVersion? BasedOnSystemTemplateVersion { get; set; }
    public Portfolio? Portfolio { get; set; }
}

/// <summary>Frozen content and provenance used for an approved or automatically sent notice.</summary>
public sealed class RenderedNotice
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public int NoticeDraftId { get; set; }
    public int WorkspaceNoticeTemplateVersionId { get; set; }
    public int LeaseManagementId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public string TemplateProvenance { get; set; } = string.Empty;
    public string? JurisdictionCode { get; set; }
    public DateTime RenderedAtUtc { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
}

/// <summary>One durable destination and its provider/outbox evidence.</summary>
public sealed class NoticeDeliveryEvidence
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public long RenderedNoticeId { get; set; }
    public int RecipientTenantId { get; set; }
    public NoticeRecipientRole RecipientRole { get; set; }
    public NoticeDeliveryChannel Channel { get; set; }
    public string Destination { get; set; } = string.Empty;
    public long OutboxMessageId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public RenderedNotice? RenderedNotice { get; set; }
    public OutboxMessage? OutboxMessage { get; set; }
}

/// <summary>Concrete automation candidate. Workers lease these rows with a fencing token.</summary>
public sealed class TenantNoticeWorkItem
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public int TenantNoticePolicyId { get; set; }
    public int LeaseManagementId { get; set; }
    public DateTime DueAtUtc { get; set; }
    public TenantNoticeWorkStatus Status { get; set; } = TenantNoticeWorkStatus.Pending;
    public string BusinessKey { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public string? ClaimOwner { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTime? ClaimExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
