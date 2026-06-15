using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Auth;

/// <summary>
/// Account-enumeration resistance for POST /auth/resend-verification (TSK-289): the endpoint must
/// return the SAME neutral message regardless of whether the email belongs to a real account, so a
/// logged-out attacker cannot probe which addresses are registered. The "already verified" branch is
/// the one permitted exception — that is not a signal an attacker can act on and helps real users.
/// </summary>
public class AuthControllerResendVerificationTests
{
    private const string NeutralMessage = "If an account exists, a verification email has been sent.";

    [Fact]
    public async Task ResendVerification_returns_neutral_message_for_unknown_account()
    {
        // Service signals "no such account" via a success result with no error (the no-enumeration no-op).
        var authService = new Mock<IAuthService>();
        authService
            .Setup(s => s.ResendVerificationEmailAsync("ghost@nobody.test"))
            .ReturnsAsync(AuthUserResult.Ok(null!));

        var controller = CreateController(authService.Object);

        var result = await controller.ResendVerification(new ResendVerificationRequest { Email = "ghost@nobody.test" });

        MessageOf(result).Should().Be(NeutralMessage);
    }

    [Fact]
    public async Task ResendVerification_returns_same_neutral_message_for_existing_unverified_account()
    {
        // An existing, still-unconfirmed account also returns a bare success — the response MUST be
        // byte-for-byte identical to the unknown-account case, otherwise it leaks existence.
        var authService = new Mock<IAuthService>();
        authService
            .Setup(s => s.ResendVerificationEmailAsync("real@user.test"))
            .ReturnsAsync(AuthUserResult.Ok(null!));

        var controller = CreateController(authService.Object);

        var result = await controller.ResendVerification(new ResendVerificationRequest { Email = "real@user.test" });

        MessageOf(result).Should().Be(NeutralMessage);
    }

    private static string? MessageOf(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        // The controller returns an anonymous object { message = ... }; read it reflectively.
        var value = ok.Value!;
        return value.GetType().GetProperty("message")?.GetValue(value) as string;
    }

    private static AuthController CreateController(IAuthService authService)
    {
        var tokenService = new Mock<IJwtTokenService>();
        var googleAuthService = new Mock<IGoogleAuthService>();
        var googleOptions = Options.Create(new GoogleAuthOptions());
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(env => env.EnvironmentName).Returns(Environments.Development);
        var configuration = new ConfigurationBuilder().Build();

        return new AuthController(
            authService,
            tokenService.Object,
            googleAuthService.Object,
            googleOptions,
            environment.Object,
            configuration,
            NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }
}
