using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface INotificationFoundationService
{
    Task<MyAlertsResponse> GetMyAlertsAsync(int portfolioId, int userId, CancellationToken ct);
    Task<MyAlertsResponse> UpdateMyAlertsAsync(WorkspaceReadScope scope, UpdateMyAlertsRequest request,
        string operationKey, CancellationToken ct);
    Task<MorningBriefingSettingsResponse> GetMorningBriefingSettingsAsync(int portfolioId, CancellationToken ct);
    Task<MorningBriefingSettingsResponse> UpdateMorningBriefingSettingsAsync(
        WorkspaceReadScope scope, UpdateMorningBriefingSettingsRequest request,
        string operationKey, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRuleResponse>> ListTeamRoutingRulesAsync(int portfolioId, CancellationToken ct);
    Task<TeamRoutingRuleResponse> ReplaceTeamRoutingRuleAsync(WorkspaceReadScope scope,
        UpsertTeamRoutingRuleRequest request, string operationKey, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRuleRecipientResponse>> ListTeamRoutingRuleRecipientsAsync(int portfolioId, int ruleId, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRecipientPreview>> PreviewTeamRoutingAsync(int portfolioId, int ruleId, CancellationToken ct);
    Task<IReadOnlyList<TenantNoticePolicyResponse>> ListTenantNoticePoliciesAsync(int portfolioId, CancellationToken ct);
    Task<IReadOnlyList<TenantNoticeRecipientPreviewResponse>> PreviewTenantNoticeRecipientsAsync(
        int portfolioId, string automationKey, int leaseManagementId, CancellationToken ct);
    Task<TenantNoticePolicyResponse> UpsertTenantNoticePolicyAsync(WorkspaceReadScope scope,
        UpsertTenantNoticePolicyRequest request, string operationKey, CancellationToken ct);
    Task<IReadOnlyList<WorkspaceNoticeTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct);
    IReadOnlyList<NoticeMergeFieldHelpResponse> ListMergeFields(string systemKey);
    Task<NoticePreviewResponse> PreviewNoticeAsync(
        int portfolioId, NoticePreviewRequest request, CancellationToken ct);
    Task<NoticeTestSendResponse> SendNoticeTestAsync(
        int portfolioId, NoticeTestSendRequest request, string operationKey, CancellationToken ct);
    Task SeedSuppliedTemplatesAsync(WorkspaceReadScope scope, string operationKey, CancellationToken ct);
    Task<TenantNoticePolicyResponse> CreateTemplateVersionAsync(WorkspaceReadScope scope, string systemKey,
        CreateWorkspaceNoticeTemplateVersionRequest request, string operationKey, CancellationToken ct);
    Task<TenantNoticePolicyResponse> RestoreDefaultAsync(WorkspaceReadScope scope, string systemKey,
        string operationKey, CancellationToken ct);
    Task<long> ApproveAndQueueAsync(
        NoticeApprovalExecutionContext context,
        int draftId,
        ApproveAndQueueNoticeRequest request,
        TenantNoticeWorkFence? workFence,
        string operationKey,
        CancellationToken ct);
    Task<IReadOnlyList<NoticeDeliveryStatusResponse>> ListDeliveryStatusesAsync(int portfolioId, int take, CancellationToken ct);
}

public sealed record NoticeApprovalExecutionContext(
    int PortfolioId,
    int? ActorUserId,
    Guid? AuthSessionId,
    int? AccessContextId,
    long? ExpectedAccessRevision)
{
    public static NoticeApprovalExecutionContext ForWorkspace(WorkspaceReadScope scope) =>
        new(scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    public static NoticeApprovalExecutionContext ForAutomation(int portfolioId) =>
        new(portfolioId, null, null, null, null);
}
