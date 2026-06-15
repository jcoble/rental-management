using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Auth;

/// <summary>
/// Security invariant for the platform-operator gate (F6 / TSK-212): the Engine Health endpoint —
/// and any future surface using the <c>PlatformAdmin</c> policy — must reject ordinary landlord
/// <c>Admin</c>s. Every landlord holds the Admin role on their OWN portfolio, so the Admin role is
/// NOT a platform credential; only an email on the <c>PlatformAdmin:Emails</c> allowlist is.
///
/// These exercise the REAL authorization pipeline (the policy registered by
/// <see cref="PlatformAdminPolicy.Register"/>, evaluated by ASP.NET's <see cref="IAuthorizationService"/>)
/// rather than merely asserting the attribute is present, and run hermetically (no DB / app boot).
/// </summary>
public class PlatformAdminPolicyTests
{
    private const string SuperAdminEmail = "operator@platform.example";

    private static IAuthorizationService BuildAuthService(params string[] allowlistEmails)
    {
        var allowlist = PlatformAdminPolicy.BuildAllowlist(
            new PlatformAdminOptions { Emails = allowlistEmails });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => PlatformAdminPolicy.Register(options, allowlist));
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal User(string? email, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "42") };
        if (email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    [Fact]
    public async Task LandlordAdmin_NotOnAllowlist_IsDenied()
    {
        // A normal customer landlord: authenticated, holds the Admin role on their portfolio, but
        // their email is NOT on the platform allowlist. They must NOT pass the platform gate.
        var auth = BuildAuthService(SuperAdminEmail);
        var landlord = User("landlord@example.com", "Admin");

        var result = await auth.AuthorizeAsync(landlord, resource: null, PlatformAdminPolicy.Name);

        result.Succeeded.Should().BeFalse(
            "the Admin role is per-portfolio and must never grant access to platform-operator surfaces");
    }

    [Fact]
    public async Task AllowlistedEmail_IsAllowed()
    {
        // Positive control: an allowlisted operator passes — proving the policy isn't always-deny.
        var auth = BuildAuthService(SuperAdminEmail);
        var op = User(SuperAdminEmail, "Admin");

        var result = await auth.AuthorizeAsync(op, resource: null, PlatformAdminPolicy.Name);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AllowlistedEmail_IsCaseInsensitive()
    {
        var auth = BuildAuthService(SuperAdminEmail);
        var op = User(SuperAdminEmail.ToUpperInvariant(), "Admin");

        var result = await auth.AuthorizeAsync(op, resource: null, PlatformAdminPolicy.Name);

        result.Succeeded.Should().BeTrue("the allowlist is compared case-insensitively");
    }

    [Fact]
    public async Task EmptyAllowlist_DeniesEveryone_FailClosed()
    {
        // When PLATFORM_ADMIN_EMAILS is unset (empty allowlist), the gate fails closed: no one,
        // not even the email that would otherwise be an operator, is a platform admin.
        var auth = BuildAuthService(/* no emails */);
        var wouldBeOperator = User(SuperAdminEmail, "Admin");

        var result = await auth.AuthorizeAsync(wouldBeOperator, resource: null, PlatformAdminPolicy.Name);

        result.Succeeded.Should().BeFalse("an empty allowlist must grant no platform access");
    }

    [Fact]
    public async Task AuthenticatedUserWithNoEmailClaim_IsDenied()
    {
        var auth = BuildAuthService(SuperAdminEmail);
        var noEmail = User(email: null, "Admin");

        var result = await auth.AuthorizeAsync(noEmail, resource: null, PlatformAdminPolicy.Name);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void EngineStatusController_IsGatedByPlatformAdminPolicy()
    {
        // The platform-internal Engine Health endpoint must carry the PlatformAdmin policy — guards
        // against a future edit silently swapping it back to a plain [Authorize(Roles="Admin")].
        var authorize = typeof(AdminEngineStatusController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be(PlatformAdminPolicy.Name);
        authorize.Roles.Should().BeNullOrEmpty(
            "platform surfaces must be email-allowlist gated, never role-gated (no Admin-OR-superadmin fallback)");
    }
}
