using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Navigation;

namespace RentalCommand.Api.Services.Domain;

public interface INotificationService
{
    Task<IReadOnlyList<NotificationResponse>> ListAsync(
        WorkspaceReadScope scope,
        NavigationExperience experience,
        bool unreadOnly = false,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default);

    Task<NotificationResponse?> GetAsync(
        WorkspaceReadScope scope,
        NavigationExperience experience,
        int notificationId,
        CancellationToken ct = default);

    Task<int> GetUnreadCountAsync(int portfolioId, int userId, CancellationToken ct = default);
    Task<bool> MarkAsReadAsync(WorkspaceReadScope scope, int notificationId, string operationKey,
        CancellationToken ct = default);
    Task MarkAllAsReadAsync(WorkspaceReadScope scope, string operationKey, CancellationToken ct = default);
    Task<NotificationResponse> CreateBroadcastAsync(WorkspaceReadScope scope,
        CreateBroadcastNotificationRequest request, string operationKey, CancellationToken ct = default);
}
