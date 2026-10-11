using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Engine.Extensions;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Notifications;

public sealed class NotificationDeliveryModeTests
{
    private static readonly NotificationDeliveryContext Delivery = new(639, "local-delivery-boundary", 1);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Capture", false)]
    [InlineData("SendGrid", false)]
    [InlineData("External", true)]
    [InlineData("external", true)]
    public void External_delivery_requires_explicit_external_mode(string? deliveryMode, bool expected)
    {
        NotificationChannelRegistration.UseExternalDelivery(deliveryMode)
            .Should().Be(expected);
    }

    [Fact]
    public void Provider_credentials_alone_do_not_enable_external_delivery()
    {
        var config = new NotificationsConfig
        {
            SendGrid = new SendGridOptions { ApiKey = "SG.test", FromEmail = "from@example.test" },
            Twilio = new TwilioOptions
            {
                AccountSid = "sid",
                AuthToken = "token",
                FromNumber = "+15555550199"
            }
        };

        config.SendGrid.Enabled.Should().BeTrue();
        config.Twilio.Enabled.Should().BeTrue();
        NotificationChannelRegistration.UseExternalDelivery(config.DeliveryMode)
            .Should().BeFalse();
    }

    [Fact]
    public void Registration_uses_capture_channel_when_delivery_mode_is_not_external()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:SendGrid:ApiKey"] = "SG.test",
                ["Notifications:SendGrid:FromEmail"] = "from@example.test"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddNotificationDeliveryChannel(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<INotificationChannel>()
            .Should().BeOfType<CapturedNotificationChannel>();
    }

    [Fact]
    public async Task Captured_channel_accepts_email_and_sms_without_external_provider()
    {
        var channel = new CapturedNotificationChannel(NullLogger<CapturedNotificationChannel>.Instance);

        var email = await channel.SendEmailAsync(
            "yara@example.test",
            "Confirm your Rental Command email",
            "Body",
            Delivery);
        var sms = await channel.SendSmsAsync("+15555550100", "Vendor reminder", Delivery, portfolioId: 17);

        email.Provider.Should().Be("local-capture");
        email.ProviderMessageId.Should().Be("captured-email-639");
        sms.Provider.Should().Be("local-capture");
        sms.ProviderMessageId.Should().Be("captured-sms-639");
    }
}
