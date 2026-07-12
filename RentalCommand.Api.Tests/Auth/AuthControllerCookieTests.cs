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
                null,
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

    // Security invariant: a web (browser) caller — which does NOT send X-Client-Type:
    // mobile — must never receive the refresh token in the JSON body. The token stays
    // confined to the httpOnly cookie so browser JS can't read it.
    [Fact]
    public async Task Login_without_mobile_header_does_not_put_refresh_token_in_body()
    {
        var tokens = CreateTokens("issued-refresh-token");
        var authService = new Mock<IAuthService>();
        authService
            .Setup(service => service.LoginAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(AuthResult.Ok(CreateLoginResponse(tokens), tokens));

        var controller = CreateController(authService.Object);

        var result = await controller.Login(new LoginRequest
        {
            Email = "admin@rentalcommand.local",
            Password = "Admin123!"
        });

        var body = ((OkObjectResult)result.Result!).Value.Should().BeOfType<LoginResponse>().Subject;
        body.RefreshToken.Should().BeNull();
    }

    // The mobile client opts in via X-Client-Type: mobile and DOES get the rotated
    // refresh token in the body (it has no readable httpOnly cookie jar). The cookie
    // is still set as well.
    [Fact]
    public async Task Login_with_mobile_header_returns_refresh_token_in_body()
    {
        var tokens = CreateTokens("issued-refresh-token");
        var authService = new Mock<IAuthService>();
        authService
            .Setup(service => service.LoginAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(AuthResult.Ok(CreateLoginResponse(tokens), tokens));

        var controller = CreateController(authService.Object);
        controller.Request.Headers["X-Client-Type"] = "mobile";

        var result = await controller.Login(new LoginRequest
        {
            Email = "admin@rentalcommand.local",
            Password = "Admin123!"
        });

        var body = ((OkObjectResult)result.Result!).Value.Should().BeOfType<LoginResponse>().Subject;
        body.RefreshToken.Should().Be("issued-refresh-token");
        controller.Response.Headers.SetCookie.ToString()
            .Should().Contain("rc_refresh_token=issued-refresh-token");
    }

    [Fact]
    public async Task Logout_never_calls_the_removed_legacy_refresh_token_store()
    {
        var tokenService = new Mock<IJwtTokenService>();

        var controller = CreateController(Mock.Of<IAuthService>(), tokenService);
        controller.Request.Headers.Cookie = "rc_refresh_token=presented-refresh-token";

        var result = await controller.Logout();

        result.Should().BeOfType<OkObjectResult>();
        tokenService.Verify(t => t.RevokeRefreshTokenAsync(It.IsAny<string>()), Times.Never);
        tokenService.Verify(t => t.RevokeRefreshTokenFamilyAsync(It.IsAny<string>()), Times.Never);
    }

    private static AuthController CreateController(IAuthService authService)
        => CreateController(authService, new Mock<IJwtTokenService>());

    private static AuthController CreateController(IAuthService authService, Mock<IJwtTokenService> tokenService)
    {
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
            Mock.Of<IAtomicAuthSessionCredentialService>(),
            Mock.Of<ICanonicalAccessTokenService>(),
            Mock.Of<RentalCommand.Core.Authorization.IAccessEnvelopeQuery>(),
            Mock.Of<RentalCommand.Core.Authorization.IEffectiveAccessContextSelectionQuery>(),
            TimeProvider.System,
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
