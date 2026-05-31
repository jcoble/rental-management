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

public class AuthControllerCookieTests
{
    [Fact]
    public async Task Login_sets_the_namespaced_refresh_cookie()
    {
        var tokens = CreateTokens("issued-refresh-token");
        var authService = new Mock<IAuthService>();
        authService
            .Setup(service => service.LoginAsync(
                "admin@rentalcommand.local",
                "Admin123!",
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(AuthResult.Ok(CreateLoginResponse(tokens), tokens));

        var controller = CreateController(authService.Object);

        var result = await controller.Login(new LoginRequest
        {
            Email = "admin@rentalcommand.local",
            Password = "Admin123!"
        });

        result.Result.Should().BeOfType<OkObjectResult>();
        controller.Response.Headers.SetCookie.ToString()
            .Should().Contain("rc_refresh_token=issued-refresh-token");
    }

    [Fact]
    public async Task Refresh_reads_the_namespaced_refresh_cookie()
    {
        var tokens = CreateTokens("rotated-refresh-token");
        var authService = new Mock<IAuthService>();
        authService
            .Setup(service => service.RefreshAsync(
                "presented-refresh-token",
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(AuthResult.Ok(CreateLoginResponse(tokens), tokens))
            .Verifiable();

        var controller = CreateController(authService.Object);
        controller.Request.Headers.Cookie = "rc_refresh_token=presented-refresh-token";

        var result = await controller.Refresh();

        result.Result.Should().BeOfType<OkObjectResult>();
        authService.Verify();
        controller.Response.Headers.SetCookie.ToString()
            .Should().Contain("rc_refresh_token=rotated-refresh-token");
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

    private static TokenResult CreateTokens(string refreshToken)
    {
        var now = DateTime.UtcNow;
        return new TokenResult
        {
            AccessToken = "access-token",
            RefreshToken = refreshToken,
            AccessTokenExpiration = now.AddMinutes(15),
            RefreshTokenExpiration = now.AddDays(7)
        };
    }

    private static LoginResponse CreateLoginResponse(TokenResult tokens) => new()
    {
        AccessToken = tokens.AccessToken,
        AccessTokenExpiration = tokens.AccessTokenExpiration,
        User = new UserDto
        {
            Id = 1,
            Email = "admin@rentalcommand.local",
            DisplayName = "Rental Command Admin",
            Roles = ["Admin"],
            EmailVerified = true
        }
    };
}
