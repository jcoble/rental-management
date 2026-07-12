using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface INotificationFoundationService
{
    Task<MyAlertsResponse> GetMyAlertsAsync(int portfolioId, int userId, CancellationToken ct);
    Task<MyAlertsResponse> UpdateMyAlertsAsync(int portfolioId, int userId, UpdateMyAlertsRequest request, CancellationToken ct);
    Task ReplaceTeamRoutingRuleAsync(int portfolioId, UpsertTeamRoutingRuleRequest request, CancellationToken ct);
    Task<IReadOnlyList<TeamRoutingRecipientPreview>> PreviewTeamRoutingAsync(int portfolioId, int ruleId, CancellationToken ct);
    Task UpsertTenantNoticePolicyAsync(int portfolioId, int actorUserId, UpsertTenantNoticePolicyRequest request, CancellationToken ct);
    Task<IReadOnlyList<WorkspaceNoticeTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct);
    Task SeedSuppliedTemplatesAsync(int portfolioId, int actorUserId, CancellationToken ct);
    Task<WorkspaceNoticeTemplateResponse> CreateTemplateVersionAsync(int portfolioId, int actorUserId, string systemKey, CreateWorkspaceNoticeTemplateVersionRequest request, CancellationToken ct);
    Task<WorkspaceNoticeTemplateResponse> RestoreDefaultAsync(int portfolioId, int actorUserId, string systemKey, CancellationToken ct);
    Task<long> ApproveAndQueueAsync(int portfolioId, int actorUserId, int draftId, ApproveAndQueueNoticeRequest request, CancellationToken ct);
}
