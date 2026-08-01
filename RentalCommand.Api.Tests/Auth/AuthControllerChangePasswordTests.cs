using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Auth;

public sealed class AuthControllerChangePasswordTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ChangePassword_RejectsMissingIdempotencyKey(string? operationKey)
    {
        var auth = new Mock<IAuthService>(MockBehavior.Strict);
        var controller = CreateController(auth.Object);

        var result = await controller.ChangePassword(Request(), operationKey, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        auth.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChangePassword_RejectsOversizedIdempotencyKey()
    {
        var auth = new Mock<IAuthService>(MockBehavior.Strict);
        var controller = CreateController(auth.Object);

        var result = await controller.ChangePassword(Request(), new string('k', 201), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        auth.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChangePassword_ForwardsTrimmedCallerOwnedIdempotencyKey()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(service => service.ChangePasswordAsync(
                It.Is<ActiveAccessContext>(active => active.UserId == 41),
                "CurrentPassword123!",
                "ReplacementPassword123!",
                "caller-operation-41",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthUserResult.Ok(new UserDto { Id = 41 }))
            .Verifiable();
        var controller = CreateController(auth.Object);

        var result = await controller.ChangePassword(
            Request(), "  caller-operation-41  ", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        auth.Verify();
    }

    private static ChangePasswordRequest Request() => new()
    {
        CurrentPassword = "CurrentPassword123!",
        NewPassword = "ReplacementPassword123!",
    };

    private static AuthController CreateController(IAuthService authService)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.EnvironmentName).Returns(Environments.Development);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "41")], "Test")),
        };
        httpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            Guid.NewGuid(), 41, 72, 9, 3, null, null, null);

        return new AuthController(
            authService,
            Mock.Of<IGoogleAuthService>(),
            Options.Create(new GoogleAuthOptions()),
            environment.Object,
            new ConfigurationBuilder().Build(),
            Mock.Of<IAtomicAuthSessionCredentialService>(),
            Mock.Of<ICanonicalAccessTokenService>(),
            Mock.Of<IAccessEnvelopeQuery>(),
            Mock.Of<IEffectiveAccessContextSelectionQuery>(),
            new SystemAuthSecurityClock(),
            NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
