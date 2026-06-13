using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

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

    /// <summary>
    /// Resolves the per-portfolio SMS provider + decrypted credentials this landlord brought. Returns
    /// <c>null</c> when the portfolio has not configured a complete provider (provider None / missing
    /// creds), in which case the dispatcher falls back to the platform-level env credentials. Pure
    /// read; safe to call from the Engine outbox path.
    /// </summary>
    Task<SmsCredentials?> GetSmsCredentialsAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>
    /// Sends a one-off verification SMS using the supplied provider + credentials (falling back to the
    /// portfolio's saved secrets for any blank slot). Returns a fail-soft result object — a transport
    /// or config error is reported as <c>Success = false</c> with the provider's message, never thrown,
    /// so the settings UI can show a friendly error.
    /// </summary>
    Task<TestSmsResponse> SendTestSmsAsync(int portfolioId, TestSmsRequest request, CancellationToken ct = default);
}
