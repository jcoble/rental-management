using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Services.Domain;

public interface INotificationSettingsService
{
    Task<NotificationSettingsResponse> GetAdminAsync(int portfolioId, CancellationToken ct = default);
    Task<NotificationSettingsResponse> UpdateAsync(int portfolioId, UpdateNotificationSettingsRequest request, CancellationToken ct = default);

    /// <summary>
    /// Resolves the runtime config for one portfolio: stored master flags + lead/grace/reminder days
    /// + decrypted SignalWire creds, with <see cref="NotificationsConfig.ChannelPreferences"/> merged
    /// over per-type defaults so Engine workers can pick exactly the enabled channels.
    /// </summary>
    Task<NotificationsConfig> GetRuntimeAsync(int portfolioId, CancellationToken ct = default);
}
