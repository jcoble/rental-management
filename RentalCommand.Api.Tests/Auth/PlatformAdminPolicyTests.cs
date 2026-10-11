using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

/// <summary>
/// Security contract for the platform-operator gate. Authorization starts from the canonical
/// numeric JWT subject and resolves the current ApplicationUser email; bearer email and role claims
/// are never authority.
/// </summary>
public sealed class PlatformAdminPolicyTests
{
    private const string PlatformOperatorEmail = "operator@platform.example";

    [Fact]
    public async Task AllowlistedDatabaseEmail_IsAllowedFromCanonicalSubject()
    {
        using var sqlite = new SqliteTestContext();
        var user = SeedUser(sqlite.Db, PlatformOperatorEmail);
        using var provider = BuildServices(sqlite.Db, PlatformOperatorEmail);

        var result = await AuthorizeAsync(provider, CanonicalPrincipal(user.Id));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AllowlistedDatabaseEmail_IsCaseInsensitive()
    {
        using var sqlite = new SqliteTestContext();
        var user = SeedUser(sqlite.Db, PlatformOperatorEmail.ToUpperInvariant());
        using var provider = BuildServices(sqlite.Db, PlatformOperatorEmail);

        var result = await AuthorizeAsync(provider, CanonicalPrincipal(user.Id));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ForgedEmailAndRoleClaims_CannotReplaceDatabaseAuthority()
    {
        using var sqlite = new SqliteTestContext();
        var user = SeedUser(sqlite.Db, "workspace-user@example.test");
        using var provider = BuildServices(sqlite.Db, PlatformOperatorEmail);
        var principal = CanonicalPrincipal(user.Id,
        [
            new Claim(ClaimTypes.Email, PlatformOperatorEmail),
            new Claim(ClaimTypes.Role, "PlatformAdmin"),
        ]);

        var result = await AuthorizeAsync(provider, principal);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyAllowlist_DeniesEveryoneFailClosed()
    {
        using var sqlite = new SqliteTestContext();
        var user = SeedUser(sqlite.Db, PlatformOperatorEmail);
        using var provider = BuildServices(sqlite.Db);

        var result = await AuthorizeAsync(provider, CanonicalPrincipal(user.Id));

        result.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-integer")]
    [InlineData("0")]
    public async Task MissingOrInvalidCanonicalSubject_IsDenied(string? subject)
    {
        using var sqlite = new SqliteTestContext();
        SeedUser(sqlite.Db, PlatformOperatorEmail);
        using var provider = BuildServices(sqlite.Db, PlatformOperatorEmail);
        Claim[] claims = subject is null
            ? []
            : [new Claim(JwtRegisteredClaimNames.Sub, subject)];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var result = await AuthorizeAsync(provider, principal);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MappedJwtSubject_UsesTheSameCanonicalDatabaseLookup()
    {
        using var sqlite = new SqliteTestContext();
        var user = SeedUser(sqlite.Db, PlatformOperatorEmail);
        using var provider = BuildServices(sqlite.Db, PlatformOperatorEmail);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"));

        var result = await AuthorizeAsync(provider, principal);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task DeletedCanonicalUser_IsDeniedEvenWithAllowlistedEmailClaim()
    {
        using var sqlite = new SqliteTestContext();
        using var provider = BuildServices(sqlite.Db, PlatformOperatorEmail);
        var principal = CanonicalPrincipal(987654,
            [new Claim(ClaimTypes.Email, PlatformOperatorEmail)]);

        var result = await AuthorizeAsync(provider, principal);

        result.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData(typeof(AdminEngineStatusController))]
    [InlineData(typeof(AdminAuditController))]
    public void PlatformOperatorControllers_AreGatedByPlatformAdminPolicy(Type controllerType)
    {
        var authorizeAttributes = controllerType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToArray();

        // AdminAuditController also inherits the ordinary authenticated-workspace gate. ASP.NET
        // combines inherited and concrete Authorize attributes, so that extra requirement is
        // intentional; the security contract is that exactly one of the combined gates is the
        // stronger platform policy and none can substitute a legacy role claim.
        authorizeAttributes
            .Should().ContainSingle(attribute => attribute.Policy == PlatformAdminPolicy.Name);
        authorizeAttributes.Should().OnlyContain(attribute =>
            string.IsNullOrWhiteSpace(attribute.Roles));
    }

    private static ServiceProvider BuildServices(
        RentalCommandDbContext db,
        params string[] allowlistEmails)
    {
        var allowlist = PlatformAdminPolicy.BuildAllowlist(
            new PlatformAdminOptions { Emails = allowlistEmails });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddAuthorization(options => PlatformAdminPolicy.Register(options, allowlist));
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        return services.BuildServiceProvider();
    }

    private static async Task<AuthorizationResult> AuthorizeAsync(
        IServiceProvider provider,
        ClaimsPrincipal principal)
    {
        using var scope = provider.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        return await authorization.AuthorizeAsync(
            principal, resource: null, PlatformAdminPolicy.Name);
    }

    private static ClaimsPrincipal CanonicalPrincipal(
        int userId,
        IEnumerable<Claim>? additionalClaims = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
        };
        if (additionalClaims is not null)
        {
            claims.AddRange(additionalClaims);
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ApplicationUser SeedUser(RentalCommandDbContext db, string email)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Platform Operator",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }
}
