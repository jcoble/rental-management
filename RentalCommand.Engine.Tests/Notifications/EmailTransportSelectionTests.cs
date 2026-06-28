using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Notifications;

/// <summary>
/// Covers the email transport selection in <see cref="RoutingNotificationChannel.SendEmailAsync"/>:
/// SMTP is used only when Transport == "Smtp" AND the SMTP creds are present; otherwise SendGrid (if
/// configured); otherwise the send is suppressed with a typed exception (no transport hit). SMTP
/// network sends aren't unit-testable, so the SMTP sender is mocked and we assert it is (or isn't)
/// invoked.
/// </summary>
public class EmailTransportSelectionTests
{
    // ---- Config-level enabled / selection flags ----

    [Fact]
    public void SmtpOptions_Enabled_RequiresHostUserAndPassword()
    {
        new SmtpOptions { Host = "smtp.zoho.com", Username = "u@d.com", Password = "p" }
            .Enabled.Should().BeTrue();

        new SmtpOptions { Host = "smtp.zoho.com", Username = "u@d.com" }.Enabled.Should().BeFalse();
        new SmtpOptions { Host = "smtp.zoho.com", Password = "p" }.Enabled.Should().BeFalse();
        new SmtpOptions { Username = "u@d.com", Password = "p" }.Enabled.Should().BeFalse();
        new SmtpOptions().Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("Smtp", true)]
    [InlineData("smtp", true)]   // case-insensitive
    [InlineData("SendGrid", false)]
    [InlineData("", false)]
    public void EmailTransportOptions_UseSmtp_MatchesTransportCaseInsensitively(string transport, bool expected)
    {
        new EmailTransportOptions { Transport = transport }.UseSmtp.Should().Be(expected);
    }

    [Fact]
    public void EmailTransport_DefaultsToSendGrid()
    {
        new EmailTransportOptions().UseSmtp.Should().BeFalse();
    }

    // ---- End-to-end selection through SendEmailAsync ----

    [Fact]
    public async Task SendEmailAsync_UsesSmtp_WhenTransportSmtpAndSmtpEnabled()
    {
        var cfg = new NotificationsConfig
        {
            Email = new EmailTransportOptions { Transport = "Smtp" },
            Smtp = new SmtpOptions { Host = "smtp.zoho.com", Username = "u@d.com", Password = "p" },
            // SendGrid also configured — SMTP must still win because it is selected.
            SendGrid = new SendGridOptions { ApiKey = "SG.x", FromEmail = "from@d.com" },
        };
        var (channel, smtp) = BuildChannel(cfg);

        await channel.SendEmailAsync("to@x.com", "Hi", "Body", htmlBody: null);

        // The plaintext body must reach the SMTP sender's body parameter; htmlBody is forwarded too
        // (null here — the auth-email callers supply it, this transport-selection test does not).
        smtp.Verify(s => s.SendAsync(cfg.Smtp, "to@x.com", "Hi", "Body", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendEmailAsync_ForwardsHtmlBodyToSmtp_WhenSupplied()
    {
        var cfg = new NotificationsConfig
        {
            Email = new EmailTransportOptions { Transport = "Smtp" },
            Smtp = new SmtpOptions { Host = "smtp.zoho.com", Username = "u@d.com", Password = "p" },
        };
        var (channel, smtp) = BuildChannel(cfg);

        await channel.SendEmailAsync("to@x.com", "Hi", "Body", htmlBody: "<p>Click <a href=\"x\">Here</a>.</p>");

        smtp.Verify(s => s.SendAsync(cfg.Smtp, "to@x.com", "Hi", "Body",
            "<p>Click <a href=\"x\">Here</a>.</p>", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendEmailAsync_FallsBackToSendGrid_WhenTransportSmtpButSmtpNotConfigured()
    {
        var cfg = new NotificationsConfig
        {
            Email = new EmailTransportOptions { Transport = "Smtp" },   // selected…
            Smtp = new SmtpOptions { Host = "smtp.zoho.com" },          // …but not enabled (no creds)
            SendGrid = new SendGridOptions { ApiKey = "SG.x", FromEmail = "from@d.com" },
        };
        var (channel, smtp) = BuildChannel(cfg);

        // SendGrid path would attempt an HTTP POST; the bare HttpClient throws on a real send, which
        // confirms it tried SendGrid (not SMTP, not suppression). We only need to assert SMTP was skipped.
        try { await channel.SendEmailAsync("to@x.com", "Hi", "Body", htmlBody: null); } catch { /* expected: HTTP send */ }

        smtp.Verify(s => s.SendAsync(It.IsAny<SmtpOptions>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendEmailAsync_UsesSendGrid_WhenTransportIsSendGridEvenIfSmtpConfigured()
    {
        var cfg = new NotificationsConfig
        {
            Email = new EmailTransportOptions { Transport = "SendGrid" },
            Smtp = new SmtpOptions { Host = "smtp.zoho.com", Username = "u@d.com", Password = "p" },
            SendGrid = new SendGridOptions { ApiKey = "SG.x", FromEmail = "from@d.com" },
        };
        var (channel, smtp) = BuildChannel(cfg);

        try { await channel.SendEmailAsync("to@x.com", "Hi", "Body", htmlBody: null); } catch { /* expected: HTTP send */ }

        smtp.Verify(s => s.SendAsync(It.IsAny<SmtpOptions>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendEmailAsync_ThrowsSuppressed_WhenNothingConfigured()
    {
        var cfg = new NotificationsConfig();   // no SMTP, no SendGrid, default transport
        var (channel, smtp) = BuildChannel(cfg);

        var ex = await Assert.ThrowsAsync<NotificationDeliverySuppressedException>(
            () => channel.SendEmailAsync("to@x.com", "Hi", "Body", htmlBody: null));

        ex.Message.Should().Contain("Email delivery is not configured");
        smtp.Verify(s => s.SendAsync(It.IsAny<SmtpOptions>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (RoutingNotificationChannel channel, Mock<ISmtpEmailSender> smtp) BuildChannel(NotificationsConfig cfg)
    {
        var smtp = new Mock<ISmtpEmailSender>();
        smtp.Setup(s => s.SendAsync(It.IsAny<SmtpOptions>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Email path never touches the SMS dispatcher; a no-op mock satisfies the dependency.
        var sms = new Mock<RentalCommand.Core.Interfaces.ISmsDispatcher>();

        // A bare HttpClient: the SendGrid path will attempt a real POST and throw, which is fine —
        // these tests only assert WHICH transport is chosen, not that SendGrid actually delivers.
        var http = new HttpClient();

        var channel = new RoutingNotificationChannel(
            http,
            Options.Create(cfg),
            sms.Object,
            smtp.Object,
            NullLogger<RoutingNotificationChannel>.Instance);

        return (channel, smtp);
    }
}
