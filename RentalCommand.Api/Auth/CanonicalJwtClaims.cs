using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace RentalCommand.Api.Auth;

public static class CanonicalJwtClaims
{
    public static string? FindSubjectValue(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? principal.FindFirstValue("sub");

    public static bool TryReadSubjectUserId(this ClaimsPrincipal principal, out int userId) =>
        int.TryParse(principal.FindSubjectValue(), out userId) && userId > 0;
}
