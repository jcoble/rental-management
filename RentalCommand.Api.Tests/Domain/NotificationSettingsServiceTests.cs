using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class NotificationSettingsServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private NotificationSettingsService NewService() =>
        new(_ctx.Db, new EphemeralDataProtectionProvider(), Array.Empty<ISmsProvider>(), TimeProvider.System);

    [Fact]
    public async Task UpdateAsync_EncryptsProviderSecrets_AndCredentialsDecryptThem()
    {
        const int portfolioId = 1;
        var sut = NewService();

        await sut.UpdateAsync(portfolioId, new UpdateNotificationSettingsRequest
        {
            EnableRentCharges = true,
            EnableLateFees = true,
            EnableLeaseExpiryReminders = true,
            NotifyTenants = true,
            RentChargeLeadDays = 7,
            LateFeeGraceDays = 3,
            LeaseExpiryReminderDays = 45,
            EnableDailyBriefingMessages = true,
            EnableRecurringMaintenance = false,
            DailyBriefingSendHourLocal = 8,
            DailyBriefingIncludeEmpty = false,
            DailyBriefingSmsRecipients = ["+13303966191"],
            ChannelPreferences =
            [
                new NotificationChannelPreferenceDto
                {
                    NotificationType = NotificationType.LateFee,
                    EnableInApp = true,
                    EnableEmail = false,
                    EnableSms = true,
                },
            ],
            SmsProvider = "SignalWire",
            SmsCredentialA = "project-id",
            SmsCredentialB = "super-secret-token",
            SmsCredentialC = "retailreadyedi.signalwire.com",
            SmsFromNumber = "+13302933081",
        });

        var row = _ctx.Db.NotificationSettings.Single();
        row.PortfolioId.Should().Be(portfolioId);
        // The secret is encrypted at rest (not stored plaintext).
        row.SmsCredentialBCipherText.Should().NotBeNull();
        row.SmsCredentialBCipherText.Should().NotContain("super-secret-token");

        var admin = await sut.GetAdminAsync(portfolioId);
        admin.EnableRentCharges.Should().BeTrue();
        admin.EnableRecurringMaintenance.Should().BeFalse();
        admin.SmsProvider.Should().Be("SignalWire");
        admin.SmsFromNumber.Should().Be("+13302933081");
        admin.SmsCredentialASet.Should().BeTrue();
        admin.SmsCredentialBSet.Should().BeTrue();
        admin.SmsCredentialCSet.Should().BeTrue();

        // The full matrix comes back (one row per NotificationType), with the saved LateFee row.
        admin.ChannelPreferences.Should().HaveCount(Enum.GetValues<NotificationType>().Length);
        var lateFeePref = admin.ChannelPreferences.Single(p => p.NotificationType == NotificationType.LateFee);
        lateFeePref.EnableSms.Should().BeTrue();

        // Decrypted credentials round-trip and are marked complete for the chosen provider.
        var creds = await sut.GetSmsCredentialsAsync(portfolioId);
        creds.Should().NotBeNull();
        creds!.Provider.Should().Be(SmsProviderKey.SignalWire);
        creds.CredentialB.Should().Be("super-secret-token");
        creds.FromNumber.Should().Be("+13302933081");
        creds.IsComplete.Should().BeTrue();

        var runtime = await sut.GetRuntimeAsync(portfolioId);
        runtime.RentChargeLeadDays.Should().Be(7);
        runtime.EnableRecurringMaintenance.Should().BeFalse();
        runtime.DailyBriefing.SmsRecipients.Should().ContainSingle().Which.Should().Be("+13303966191");
    }

    [Fact]
    public async Task GetSmsCredentialsAsync_ReturnsNull_WhenNoProviderConfigured()
    {
        var sut = NewService();

        // A portfolio that never configured SMS → null, so the dispatcher falls back to platform env.
        var creds = await sut.GetSmsCredentialsAsync(99);
        creds.Should().BeNull();
    }

    [Fact]
    public async Task GetSmsCredentialsAsync_ReturnsNull_WhenProviderChosenButCredentialsIncomplete()
    {
        const int portfolioId = 5;
        var sut = NewService();

        // Telnyx selected but no API key → incomplete → null (fail-soft to platform fallback).
        await sut.UpdateAsync(portfolioId, new UpdateNotificationSettingsRequest
        {
            SmsProvider = "Telnyx",
            SmsFromNumber = "+13302933081",
            // CredentialA (Telnyx API key) deliberately omitted.
        });

        var creds = await sut.GetSmsCredentialsAsync(portfolioId);
        creds.Should().BeNull();
    }
}
