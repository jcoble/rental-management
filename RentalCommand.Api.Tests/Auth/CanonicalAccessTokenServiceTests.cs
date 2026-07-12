using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Auth;

public sealed class CanonicalAccessTokenServiceTests
{
    [Fact]
    public void Issue_ContainsOnlyProtocolAndCanonicalAccessCoordinates()
    {
        var settings = Options.Create(new JwtSettings
        {
            SecretKey = new string('s', 64),
            Issuer = "tests",
            Audience = "tests",
            AccessTokenExpirationMinutes = 15,
            RefreshTokenExpirationDays = 7,
        });
        var sessionId = Guid.NewGuid();
        var sut = new CanonicalAccessTokenService(settings, TimeProvider.System);

        var issued = sut.Issue(new CanonicalAccessCoordinates(41, sessionId, 72, 9));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.Token);
        var claims = jwt.Claims.ToLookup(claim => claim.Type, claim => claim.Value);

        claims[JwtRegisteredClaimNames.Sub].Should().ContainSingle().Which.Should().Be("41");
        claims["sid"].Should().ContainSingle().Which.Should().Be(sessionId.ToString("D"));
        claims["ctx"].Should().ContainSingle().Which.Should().Be("72");
        claims["ar"].Should().ContainSingle().Which.Should().Be("9");
        claims["portfolioId"].Should().BeEmpty();
        claims["tenantId"].Should().BeEmpty();
        claims["ownerEntityId"].Should().BeEmpty();
        claims["role"].Should().BeEmpty();
        claims["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"].Should().BeEmpty();
    }
}
