using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Services.Domain;

public interface INotificationSettingsService
{
    Task<NotificationSettingsResponse> GetAdminAsync(CancellationToken ct = default);
    Task<NotificationSettingsResponse> UpdateAsync(UpdateNotificationSettingsRequest request, CancellationToken ct = default);
    Task<NotificationsConfig> GetRuntimeAsync(CancellationToken ct = default);
}
