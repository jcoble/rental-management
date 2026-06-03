using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class NotificationSettingsServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task UpdateAsync_EncryptsProviderSecrets_AndRuntimeDecryptsThem()
    {
        var sut = new NotificationSettingsService(
            _ctx.Db,
            new EphemeralDataProtectionProvider());

        await sut.UpdateAsync(new UpdateNotificationSettingsRequest
        {
            EnableRentCharges = true,
            EnableLateFees = true,
            EnableLeaseExpiryReminders = true,
            NotifyTenants = true,
            RentChargeLeadDays = 7,
            LateFeeGraceDays = 3,
            LeaseExpiryReminderDays = 45,
            EnableDailyBriefingMessages = true,
            DailyBriefingSendHourLocal = 8,
            DailyBriefingIncludeEmpty = false,
            DailyBriefingSmsRecipients = ["+13303966191"],
            SignalWireProjectId = "project-id",
            SignalWireToken = "super-secret-token",
            SignalWireSpaceUrl = "retailreadyedi.signalwire.com",
            SignalWireFromNumber = "+13302933081",
        });

        var row = _ctx.Db.NotificationSettings.Single();
        row.SignalWireTokenCipherText.Should().NotBe("super-secret-token");
        row.SignalWireTokenCipherText.Should().NotContain("super-secret-token");

        var admin = await sut.GetAdminAsync();
        admin.EnableRentCharges.Should().BeTrue();
        admin.EnableLateFees.Should().BeTrue();
        admin.EnableLeaseExpiryReminders.Should().BeTrue();
        admin.NotifyTenants.Should().BeTrue();
        admin.RentChargeLeadDays.Should().Be(7);
        admin.LateFeeGraceDays.Should().Be(3);
        admin.LeaseExpiryReminderDays.Should().Be(45);
        admin.SignalWireTokenSet.Should().BeTrue();
        admin.SignalWireToken.Should().BeNull();

        var runtime = await sut.GetRuntimeAsync();
        runtime.EnableRentCharges.Should().BeTrue();
        runtime.EnableLateFees.Should().BeTrue();
        runtime.EnableLeaseExpiryReminders.Should().BeTrue();
        runtime.NotifyTenants.Should().BeTrue();
        runtime.RentChargeLeadDays.Should().Be(7);
        runtime.LateFeeGraceDays.Should().Be(3);
        runtime.LeaseExpiryReminderDays.Should().Be(45);
        runtime.EnableDailyBriefingMessages.Should().BeTrue();
        runtime.DailyBriefing.SmsRecipients.Should().ContainSingle().Which.Should().Be("+13303966191");
        runtime.SignalWire.Token.Should().Be("super-secret-token");
        runtime.SignalWire.Enabled.Should().BeTrue();
    }
}
