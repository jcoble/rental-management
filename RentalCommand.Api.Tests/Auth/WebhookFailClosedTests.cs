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
/// M-6 regression: the anonymous, state-changing e-sign and SMS inbound webhooks must FAIL CLOSED.
/// When the signature secret / provider auth token is unset, verification cannot run, so the request is
/// unverifiable and spoofable. Outside Development these endpoints must reject with 403 and never invoke
/// the downstream state mutation; skip-with-warning is permitted ONLY in Development.
/// </summary>
public sealed class WebhookFailClosedTests
{
    // ---- E-sign ----

    [Fact]
    public async Task Esign_unconfigured_secret_in_production_is_rejected_403_and_not_processed()
    {
        var esign = new Mock<ILeaseEsignService>(MockBehavior.Strict); // strict => any handler call fails the test
        var controller = CreateEsignController(esign, webhookSecret: null, environment: Environments.Production);

        var result = await Invoke(controller, EsignSignedPayload);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        // Strict mock asserts HandleSignedEventAsync was never reached (no state change on a forged event).
        esign.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Esign_unconfigured_secret_in_development_is_processed_with_warning()
    {
        var esign = new Mock<ILeaseEsignService>();
        var controller = CreateEsignController(esign, webhookSecret: null, environment: Environments.Development);

        var result = await Invoke(controller, EsignSignedPayload);

        // Dev: still acked (Content), and the signed handler is reached so local testing works without a secret.
        result.Should().BeOfType<ContentResult>();
        esign.Verify(e => e.HandleSignedEventAsync("sig_req_123", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- SMS ----

    [Fact]
    public async Task Sms_unconfigured_token_in_production_is_rejected_403_and_not_routed()
    {
        var router = new Mock<ISmsInboundRouter>(MockBehavior.Strict); // strict => any RouteAsync call fails the test
        var controller = CreateSmsController(router, twilioToken: null, environment: Environments.Production);

        var result = await controller.Inbound(from: "+15551234567", body: "YES", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        router.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sms_unconfigured_token_in_development_is_routed_with_warning()
    {
        var router = new Mock<ISmsInboundRouter>();
        router.Setup(r => r.RouteAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Thanks!");
        var controller = CreateSmsController(router, twilioToken: null, environment: Environments.Development);

        var result = await controller.Inbound(from: "+15551234567", body: "YES", CancellationToken.None);

        result.Should().BeOfType<ContentResult>();
        router.Verify(r => r.RouteAsync("+15551234567", "YES", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- helpers ----

    private const string EsignSignedPayload =
        "{\"event\":{\"event_type\":\"signature_request_all_signed\",\"event_time\":\"1\",\"event_hash\":\"x\"}," +
        "\"signature_request\":{\"signature_request_id\":\"sig_req_123\"}}";

    private static async Task<IActionResult> Invoke(EsignWebhookController controller, string rawJson)
    {
        controller.ControllerContext.HttpContext.Request.Body =
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));
        return await controller.Handle(CancellationToken.None);
    }

    private static EsignWebhookController CreateEsignController(
        Mock<ILeaseEsignService> esign, string? webhookSecret, string environment)
    {
        var config = Options.Create(new EsignConfig { ApiKey = "key", WebhookSecret = webhookSecret });
        var validator = new EsignWebhookSignatureValidator(config, NullLogger<EsignWebhookSignatureValidator>.Instance);
        var env = StubEnvironment(environment);

        return new EsignWebhookController(esign.Object, validator, config, env, NullLogger<EsignWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static SmsWebhookController CreateSmsController(
        Mock<ISmsInboundRouter> router, string? twilioToken, string environment)
    {
        var config = Options.Create(new NotificationsConfig
        {
            Twilio = new TwilioOptions { AuthToken = twilioToken },
        });
        var validator = new SmsWebhookSignatureValidator(config, NullLogger<SmsWebhookSignatureValidator>.Instance);
        var env = StubEnvironment(environment);

        return new SmsWebhookController(router.Object, validator, env, NullLogger<SmsWebhookController>.Instance)
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
