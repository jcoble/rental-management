using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed record MyAlertsResponse(int UserId, string DisplayName, string? Email, string? PhoneNumber,
    bool EnableInApp, bool EnableMobilePush, bool EnableEmail, bool EnableSms);
public sealed record UpdateMyAlertsRequest(bool EnableInApp, bool EnableMobilePush, bool EnableEmail, bool EnableSms);

public sealed record TeamRoutingRecipientRequest(int UserId, string Reason);
public sealed record UpsertTeamRoutingRuleRequest(TeamRoutingTopic Topic, int? PropertyId,
    bool UseWorkspaceAdministratorFallback, IReadOnlyList<TeamRoutingRecipientRequest> Recipients);
public sealed record TeamRoutingRecipientPreview(int UserId, string DisplayName, string? Email, int? PropertyId,
    string Scope, string Reason, bool IsAdministratorFallback);

public sealed record UpsertTenantNoticePolicyRequest(
    string AutomationKey, TenantNoticeMode Mode, NoticeClassification Classification, int LeadDays,
    int SendHourLocal, bool SendTenantPortal, bool SendMobilePush, bool SendEmail, bool SendSms,
    bool IncludePrimaryTenant, bool IncludeCoTenant, bool IncludeEligibleGuarantor, bool IncludeOccupant,
    NoticeFailureBehavior FailureBehavior, int WorkspaceNoticeTemplateVersionId,
    string? ReviewedJurisdictionCode, bool ConfirmJurisdictionReviewed);

public sealed record WorkspaceNoticeTemplateResponse(int Id, string SystemKey, int Version,
    int BasedOnSystemTemplateVersionId, bool IsCustomized, string Subject, string Body,
    NoticeClassification Classification, string? JurisdictionCode, DateTime? JurisdictionReviewedAtUtc,
    bool UpdateAvailable, DateTime CreatedAtUtc);
public sealed record CreateWorkspaceNoticeTemplateVersionRequest(string Subject, string Body,
    string? JurisdictionCode, bool ConfirmJurisdictionReviewed);

public sealed record ApproveAndQueueNoticeRequest(IReadOnlyList<NoticeDeliveryChannel> Channels);
