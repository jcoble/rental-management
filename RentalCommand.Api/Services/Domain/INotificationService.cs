using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface INotificationService
{
    Task<IReadOnlyList<NotificationResponse>> ListAsync(
        int portfolioId,
        int userId,
        bool unreadOnly = false,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default);

    Task<int> GetUnreadCountAsync(int portfolioId, int userId, CancellationToken ct = default);
    Task<bool> MarkAsReadAsync(WorkspaceReadScope scope, int notificationId, string operationKey,
        CancellationToken ct = default);
    Task MarkAllAsReadAsync(WorkspaceReadScope scope, string operationKey, CancellationToken ct = default);
    Task<NotificationResponse> CreateBroadcastAsync(WorkspaceReadScope scope,
        CreateBroadcastNotificationRequest request, string operationKey, CancellationToken ct = default);
}
