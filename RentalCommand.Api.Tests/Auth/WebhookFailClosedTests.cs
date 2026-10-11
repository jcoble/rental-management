using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Security;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Auth;

/// <summary>
/// M-6 regression: the anonymous, state-changing SMS inbound webhook must FAIL CLOSED.
/// When the signature secret / provider auth token is unset, verification cannot run, so the request is
/// unverifiable and spoofable. Outside Development these endpoints must reject with 403 and never invoke
/// the downstream state mutation; skip-with-warning is permitted ONLY in Development.
/// </summary>
public sealed class WebhookFailClosedTests
{
    [Fact]
    public async Task Sms_unconfigured_token_in_production_is_rejected_403_and_not_routed()
    {
        var router = new Mock<ISmsInboundRouter>(MockBehavior.Strict); // strict => any RouteAsync call fails the test
        var controller = CreateSmsController(router, twilioToken: null, environment: Environments.Production);

        var result = await controller.Inbound("SM-production-rejected", "+15551234567", "YES", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        router.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sms_unconfigured_token_in_development_is_routed_with_warning()
    {
        var router = new Mock<ISmsInboundRouter>();
        router.Setup(r => r.RouteAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Thanks!");
        var controller = CreateSmsController(router, twilioToken: null, environment: Environments.Development);

        var result = await controller.Inbound("SM-development-routed", "+15551234567", "YES", CancellationToken.None);

        result.Should().BeOfType<ContentResult>();
        router.Verify(r => r.RouteAsync(
            "SM-development-routed", "+15551234567", "YES",
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sms_missing_provider_event_id_is_rejected_before_routing()
    {
        var router = new Mock<ISmsInboundRouter>(MockBehavior.Strict);
        var controller = CreateSmsController(router, twilioToken: null, environment: Environments.Development);

        var result = await controller.Inbound(null, "+15551234567", "DONE", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        router.VerifyNoOtherCalls();
    }

    // ---- helpers ----

    private static SmsWebhookController CreateSmsController(
        Mock<ISmsInboundRouter> router, string? twilioToken, string environment)
    {
        var config = Options.Create(new NotificationsConfig
        {
            Twilio = new TwilioOptions { AuthToken = twilioToken },
        });
        var validator = new SmsWebhookSignatureValidator(config, NullLogger<SmsWebhookSignatureValidator>.Instance);
        var env = StubEnvironment(environment);

        return new SmsWebhookController(router.Object, validator, env, NullLogger<SmsWebhookController>.Instance, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static IHostEnvironment StubEnvironment(string environmentName)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return env.Object;
    }
}
