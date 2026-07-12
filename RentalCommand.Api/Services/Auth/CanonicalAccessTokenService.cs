using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Services.Auth;

public sealed record CanonicalAccessCoordinates(
    int UserId,
    Guid SessionId,
    int AccessContextId,
    long AccessRevision);

public interface ICanonicalAccessTokenService
{
    (string Token, DateTime ExpiresAtUtc) Issue(CanonicalAccessCoordinates coordinates);
}

/// <summary>
/// Issues the deliberately small production access token. Capabilities and resource ids are never
/// copied into the bearer: every authorization decision is reconstructed from the selected context
/// and its revision in PostgreSQL.
/// </summary>
public sealed class CanonicalAccessTokenService : ICanonicalAccessTokenService
{
    private readonly JwtSettings _settings;
    private readonly TimeProvider _timeProvider;

    public CanonicalAccessTokenService(IOptions<JwtSettings> settings, TimeProvider timeProvider)
    {
        _settings = settings.Value;
        _timeProvider = timeProvider;
    }

    public (string Token, DateTime ExpiresAtUtc) Issue(CanonicalAccessCoordinates coordinates)
    {
        if (coordinates.UserId <= 0 || coordinates.SessionId == Guid.Empty ||
            coordinates.AccessContextId <= 0 || coordinates.AccessRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coordinates));
        }

        var expiresAt = _timeProvider.GetUtcNow().UtcDateTime
            .AddMinutes(_settings.AccessTokenExpirationMinutes);
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));
        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, coordinates.UserId.ToString()),
                new Claim("sid", coordinates.SessionId.ToString("D")),
                new Claim("ctx", coordinates.AccessContextId.ToString()),
                new Claim("ar", coordinates.AccessRevision.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            ],
            expires: expiresAt,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
