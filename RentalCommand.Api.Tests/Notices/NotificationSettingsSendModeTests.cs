using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Notices;

public class NotificationSettingsSendModeTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private NotificationSettingsService NewService() =>
        new(_ctx.Db, new EphemeralDataProtectionProvider(), Array.Empty<ISmsProvider>());

    [Fact]
    public async Task UpdateAsync_then_GetAdminAsync_round_trips_per_type_send_mode()
    {
        const int portfolioId = 1;
        var sut = NewService();

        await sut.UpdateAsync(portfolioId, new UpdateNotificationSettingsRequest
        {
            AutoSendRentReminder = true,
            AutoSendLateRent = true,
        }, default);

        var resp = await sut.GetAdminAsync(portfolioId);

        resp.AutoSendRentReminder.Should().BeTrue();
        resp.AutoSendLateRent.Should().BeTrue();
        resp.AutoSendRenewal.Should().BeFalse();
        resp.AutoSendMonthToMonth.Should().BeFalse();
        resp.AutoSendMoveOut.Should().BeFalse();
    }
}
