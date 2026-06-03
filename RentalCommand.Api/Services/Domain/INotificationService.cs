using RentalCommand.Api.DTOs;

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
    Task<bool> MarkAsReadAsync(int portfolioId, int userId, int notificationId, CancellationToken ct = default);
    Task MarkAllAsReadAsync(int portfolioId, int userId, CancellationToken ct = default);
    Task<NotificationResponse> CreateBroadcastAsync(int portfolioId, CreateBroadcastNotificationRequest request, CancellationToken ct = default);
    Task<NotificationEmailResponse> GetNotificationEmailAsync(int portfolioId, CancellationToken ct = default);
    Task<NotificationEmailResponse?> SetNotificationEmailAsync(int portfolioId, string? email, CancellationToken ct = default);
}
