using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed record MyAlertsResponse(int UserId, string DisplayName, string? Email, string? PhoneNumber,
    bool EnableInApp, bool EnableMobilePush, bool EnableEmail, bool EnableSms);
public sealed record UpdateMyAlertsRequest(bool EnableInApp, bool EnableMobilePush, bool EnableEmail, bool EnableSms);

public sealed record MorningBriefingSettingsResponse(
    bool Enabled, int SendHourLocal, bool IncludeEmpty, string TimeZone);
public sealed record UpdateMorningBriefingSettingsRequest(
    bool Enabled, int SendHourLocal, bool IncludeEmpty);

public sealed record TeamRoutingRecipientRequest(int UserId, string Reason);
public sealed record UpsertTeamRoutingRuleRequest(TeamRoutingTopic Topic, int? PropertyId,
    bool UseWorkspaceAdministratorFallback, IReadOnlyList<TeamRoutingRecipientRequest> Recipients);
public sealed record TeamRoutingRuleResponse(int Id, TeamRoutingTopic Topic, int? PropertyId,
    string Scope, bool UseWorkspaceAdministratorFallback, int NamedRecipientCount,
    string NamedRecipientSummary, string RoutingExplanation, DateTime UpdatedAtUtc);
public sealed record TeamRoutingRuleRecipientResponse(int UserId, string DisplayName, string? Email, string Reason);
public sealed record TeamRoutingRecipientPreview(
    int UserId,
    string DisplayName,
    string? Email,
    string? PhoneNumber,
    bool EnableInApp,
    bool EnableMobilePush,
    bool EnableEmail,
    bool EnableSms,
    int? PropertyId,
    string Scope,
    string Reason,
    bool IsAdministratorFallback);

public sealed record NoticeMergeFieldHelpResponse(string Key, string Token, string Label, string Description,
    string Example);
public sealed record NoticePreviewRequest(string SystemKey, string Subject, string Body);
public sealed record NoticePreviewResponse(
    string SystemKey,
    string Subject,
    string Body,
    IReadOnlyDictionary<string, string> ExampleValues);
public sealed record NoticeTestSendRequest(
    string SystemKey,
    string Subject,
    string Body,
    string Destination);
public enum NoticeTestSendState
{
    Accepted,
    Suppressed,
    ProviderError,
}
public sealed record NoticeTestSendResponse(
    NoticeTestSendState State,
    string Message,
    string Destination,
    string? Provider,
    string? ProviderMessageId);

public sealed record TenantNoticeRecipientPreviewResponse(
    int LeaseManagementPartyId,
    int TenantId,
    string DisplayName,
    NoticeRecipientRole Role,
    bool Eligible,
    IReadOnlyList<NoticeDeliveryChannel> AvailableChannels,
    string? Email,
    string? Phone,
    string Reason);

public sealed record UpsertTenantNoticePolicyRequest(
    string AutomationKey, TenantNoticeMode Mode, NoticeClassification Classification, int LeadDays,
    int SendHourLocal, bool SendTenantPortal, bool SendMobilePush, bool SendEmail, bool SendSms,
    bool IncludePrimaryTenant, bool IncludeCoTenant, bool IncludeEligibleGuarantor, bool IncludeOccupant,
    NoticeFailureBehavior FailureBehavior, int WorkspaceNoticeTemplateVersionId,
    string? ReviewedJurisdictionCode, bool ConfirmJurisdictionReviewed);
public sealed record TenantNoticePolicyResponse(
    int Id, string AutomationKey, TenantNoticeMode Mode, NoticeClassification Classification,
    int LeadDays, int SendHourLocal, bool SendTenantPortal, bool SendMobilePush, bool SendEmail, bool SendSms,
    bool IncludePrimaryTenant, bool IncludeCoTenant, bool IncludeEligibleGuarantor, bool IncludeOccupant,
    NoticeFailureBehavior FailureBehavior, int WorkspaceNoticeTemplateVersionId,
    string TemplateSystemKey, int TemplateVersion, string TemplateSubject, string TemplateBody,
    int TemplateBasedOnSystemTemplateVersionId, string TemplateProvenance, bool TemplateIsCustomized,
    bool TemplateUpdateAvailable, DateTime TemplateCreatedAtUtc, string? TemplateJurisdictionCode,
    DateTime? TemplateJurisdictionReviewedAtUtc,
    string? ReviewedJurisdictionCode, DateTime? JurisdictionReviewedAtUtc, bool CanAutoSend, DateTime UpdatedAtUtc);

public sealed record WorkspaceNoticeTemplateResponse(int Id, string SystemKey, int Version,
    int BasedOnSystemTemplateVersionId, bool IsCustomized, string Subject, string Body,
    NoticeClassification Classification, string? JurisdictionCode, DateTime? JurisdictionReviewedAtUtc,
    bool UpdateAvailable, DateTime CreatedAtUtc);
public sealed record CreateWorkspaceNoticeTemplateVersionRequest(string Subject, string Body,
    string? JurisdictionCode, bool ConfirmJurisdictionReviewed);

public sealed record ApproveAndQueueNoticeRequest(IReadOnlyList<NoticeDeliveryChannel> Channels);
public sealed record TenantNoticeWorkFence(long WorkItemId, Guid ClaimToken);

public sealed record NoticeDeliveryStatusResponse(
    long EvidenceId,
    long RenderedNoticeId,
    int NoticeDraftId,
    string Subject,
    int LeaseManagementId,
    NoticeRecipientRole RecipientRole,
    NoticeDeliveryChannel Channel,
    string Destination,
    NoticeDeliveryState Status,
    int AttemptCount,
    DateTime CreatedAtUtc,
    DateTime? LastAttemptAtUtc,
    DateTime? NextAttemptAtUtc,
    DateTime? AcceptedAtUtc,
    DateTime? DeliveredAtUtc,
    DateTime? FailedAtUtc,
    string? Provider,
    string? ProviderMessageId,
    string? LastError);
