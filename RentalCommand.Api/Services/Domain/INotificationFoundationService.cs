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
        int portfolioId, UpdateMorningBriefingSettingsRequest request, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRuleResponse>> ListTeamRoutingRulesAsync(int portfolioId, CancellationToken ct);
    Task<TeamRoutingRuleResponse> ReplaceTeamRoutingRuleAsync(WorkspaceReadScope scope,
        UpsertTeamRoutingRuleRequest request, string operationKey, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRuleRecipientResponse>> ListTeamRoutingRuleRecipientsAsync(int portfolioId, int ruleId, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRecipientPreview>> PreviewTeamRoutingAsync(int portfolioId, int ruleId, CancellationToken ct);
    Task<IReadOnlyList<TenantNoticePolicyResponse>> ListTenantNoticePoliciesAsync(int portfolioId, CancellationToken ct);
    Task<TenantNoticePolicyResponse> UpsertTenantNoticePolicyAsync(WorkspaceReadScope scope,
        UpsertTenantNoticePolicyRequest request, string operationKey, CancellationToken ct);
    Task<IReadOnlyList<WorkspaceNoticeTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct);
    Task SeedSuppliedTemplatesAsync(WorkspaceReadScope scope, string operationKey, CancellationToken ct);
    Task<TenantNoticePolicyResponse> CreateTemplateVersionAsync(WorkspaceReadScope scope, string systemKey,
        CreateWorkspaceNoticeTemplateVersionRequest request, string operationKey, CancellationToken ct);
    Task<TenantNoticePolicyResponse> RestoreDefaultAsync(WorkspaceReadScope scope, string systemKey,
        string operationKey, CancellationToken ct);
    Task<long> ApproveAndQueueAsync(int portfolioId, int? actorUserId, int draftId,
        ApproveAndQueueNoticeRequest request, TenantNoticeWorkFence? workFence, CancellationToken ct);
    Task<IReadOnlyList<NoticeDeliveryStatusResponse>> ListDeliveryStatusesAsync(int portfolioId, int take, CancellationToken ct);
}
