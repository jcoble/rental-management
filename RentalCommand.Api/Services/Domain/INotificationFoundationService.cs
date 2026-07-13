using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface INotificationFoundationService
{
    Task<MyAlertsResponse> GetMyAlertsAsync(int portfolioId, int userId, CancellationToken ct);
    Task<MyAlertsResponse> UpdateMyAlertsAsync(int portfolioId, int userId, UpdateMyAlertsRequest request, CancellationToken ct);
    Task<MorningBriefingSettingsResponse> GetMorningBriefingSettingsAsync(int portfolioId, CancellationToken ct);
    Task<MorningBriefingSettingsResponse> UpdateMorningBriefingSettingsAsync(
        int portfolioId, UpdateMorningBriefingSettingsRequest request, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRuleResponse>> ListTeamRoutingRulesAsync(int portfolioId, CancellationToken ct);
    Task<TeamRoutingRuleResponse> ReplaceTeamRoutingRuleAsync(int portfolioId, UpsertTeamRoutingRuleRequest request, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRuleRecipientResponse>> ListTeamRoutingRuleRecipientsAsync(int portfolioId, int ruleId, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRecipientPreview>> PreviewTeamRoutingAsync(int portfolioId, int ruleId, CancellationToken ct);
    Task<IReadOnlyList<TenantNoticePolicyResponse>> ListTenantNoticePoliciesAsync(int portfolioId, CancellationToken ct);
    Task<TenantNoticePolicyResponse> UpsertTenantNoticePolicyAsync(int portfolioId, int actorUserId, UpsertTenantNoticePolicyRequest request, CancellationToken ct);
    Task<IReadOnlyList<WorkspaceNoticeTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct);
    Task SeedSuppliedTemplatesAsync(int portfolioId, int actorUserId, CancellationToken ct);
    Task<TenantNoticePolicyResponse> CreateTemplateVersionAsync(int portfolioId, int actorUserId, string systemKey, CreateWorkspaceNoticeTemplateVersionRequest request, CancellationToken ct);
    Task<TenantNoticePolicyResponse> RestoreDefaultAsync(int portfolioId, int actorUserId, string systemKey, CancellationToken ct);
    Task<long> ApproveAndQueueAsync(int portfolioId, int? actorUserId, int draftId,
        ApproveAndQueueNoticeRequest request, TenantNoticeWorkFence? workFence, CancellationToken ct);
    Task<IReadOnlyList<NoticeDeliveryStatusResponse>> ListDeliveryStatusesAsync(int portfolioId, int take, CancellationToken ct);
}
