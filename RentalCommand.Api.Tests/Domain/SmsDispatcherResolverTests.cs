using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Sms;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// The one resolver-chain behaviour that matters: a portfolio's own configured provider wins; when it
/// has none, the platform-env credentials are used as the fallback; when neither is set, the send is
/// suppressed (no throw).
/// </summary>
public class SmsDispatcherResolverTests
{
    [Fact]
    public async Task PortfolioProvider_TakesPrecedence_OverPlatformEnv()
    {
        // Portfolio configured for Telnyx; platform env configured for SignalWire. Portfolio wins.
        var portfolioCreds = new SmsCredentials(SmsProviderKey.Telnyx, "telnyx-key", null, null, "+15550001111");
        var settings = new FakeSettingsService(portfolioCreds);
        var cfg = PlatformConfigWithSignalWire();
        var (telnyx, signalwire) = (new RecordingProvider(SmsProviderKey.Telnyx), new RecordingProvider(SmsProviderKey.SignalWire));

        var sut = new SmsDispatcher(settings, Options.Create(cfg), new ISmsProvider[] { telnyx, signalwire }, NullLogger<SmsDispatcher>.Instance);

        await sut.SendAsync(portfolioId: 1, "+15559998888", "hello");

        telnyx.Sent.Should().ContainSingle();
        telnyx.Sent[0].Creds.CredentialA.Should().Be("telnyx-key");
        signalwire.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task PlatformEnv_UsedAsFallback_WhenPortfolioHasNoProvider()
    {
        // Portfolio returns null (nothing configured) → platform-env SignalWire is used.
        var settings = new FakeSettingsService(null);
        var cfg = PlatformConfigWithSignalWire();
        var signalwire = new RecordingProvider(SmsProviderKey.SignalWire);

        var sut = new SmsDispatcher(settings, Options.Create(cfg), new ISmsProvider[] { signalwire }, NullLogger<SmsDispatcher>.Instance);

        await sut.SendAsync(portfolioId: 1, "+15559998888", "hello");

        signalwire.Sent.Should().ContainSingle();
        signalwire.Sent[0].Creds.Provider.Should().Be(SmsProviderKey.SignalWire);
    }

    [Fact]
    public async Task Suppresses_WhenNeitherPortfolioNorPlatformConfigured()
    {
        var settings = new FakeSettingsService(null);
        var signalwire = new RecordingProvider(SmsProviderKey.SignalWire);

        var sut = new SmsDispatcher(settings, Options.Create(new NotificationsConfig()), new ISmsProvider[] { signalwire }, NullLogger<SmsDispatcher>.Instance);

        // No throw, nothing sent (suppression log only).
        await sut.SendAsync(portfolioId: 1, "+15559998888", "hello");

        signalwire.Sent.Should().BeEmpty();
    }

    private static NotificationsConfig PlatformConfigWithSignalWire() => new()
    {
        SignalWire = new SignalWireOptions
        {
            ProjectId = "env-project",
            Token = "env-token",
            SpaceUrl = "env.signalwire.com",
            FromNumber = "+15550000000",
        },
    };

    private sealed class RecordingProvider : ISmsProvider
    {
        public RecordingProvider(SmsProviderKey key) => Key = key;
        public SmsProviderKey Key { get; }
        public List<(SmsCredentials Creds, string To, string Message)> Sent { get; } = new();

        public Task SendAsync(SmsCredentials credentials, string toPhoneNumber, string message, CancellationToken ct = default)
        {
            Sent.Add((credentials, toPhoneNumber, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettingsService : INotificationSettingsService
    {
        private readonly SmsCredentials? _creds;
        public FakeSettingsService(SmsCredentials? creds) => _creds = creds;

        public Task<SmsCredentials?> GetSmsCredentialsAsync(int portfolioId, CancellationToken ct = default) =>
            Task.FromResult(_creds);

        public Task<NotificationSettingsResponse> GetAdminAsync(int portfolioId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NotificationSettingsResponse> UpdateAsync(int portfolioId, UpdateNotificationSettingsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NotificationsConfig> GetRuntimeAsync(int portfolioId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TestSmsResponse> SendTestSmsAsync(int portfolioId, TestSmsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
