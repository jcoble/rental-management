using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Engine.Tests.Automation;

internal sealed class FakeNotificationSettingsService : INotificationSettingsService
{
    private readonly NotificationsConfig _config;

    public FakeNotificationSettingsService(NotificationsConfig config)
    {
        _config = config;
    }

    public Task<NotificationSettingsResponse> GetAdminAsync(int portfolioId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<NotificationSettingsResponse> UpdateAsync(int portfolioId, UpdateNotificationSettingsRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<NotificationsConfig> GetRuntimeAsync(int portfolioId, CancellationToken ct = default)
        => Task.FromResult(_config);
}
