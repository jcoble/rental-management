using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

/// <summary>
/// Verifies the portal-access toggle's effect on the REAL login path: disabling a tenant's portal
/// access makes <see cref="AuthService.LoginAsync"/> reject them; re-enabling lets them sign in again.
/// Exercises the actual <c>SignInManager.CheckPasswordSignInAsync</c> lockout check — nothing is stubbed
/// in the accept/reject decision (prod auth).
/// </summary>
public sealed class TenantPortalAccessLoginTests : IDisposable
{
    private const int PortfolioId = 1;
    private const string Email = "toggle.tenant@example.local";

    private readonly SqliteTestContext _ctx = new();
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly TenantPortalProvisioningService _provisioning;

    public TenantPortalAccessLoginTests()
    {
        _userManager = CreateUserManager(_ctx.Db);
        _ctx.Db.Roles.Add(new IdentityRole<int>
        {
            Name = nameof(UserRole.Tenant),
            NormalizedName = nameof(UserRole.Tenant).ToUpperInvariant(),
        });
        _ctx.Db.SaveChanges();
        _provisioning = new TenantPortalProvisioningService(
            _userManager, _ctx.Db, Options.Create(new SeedSettings()),
            TimeProvider.System,
            NullLogger<TenantPortalProvisioningService>.Instance);
    }

    public void Dispose()
    {
        _userManager.Dispose();
        _ctx.Dispose();
    }

    [Fact]
    public async Task DisablingPortalAccess_BlocksLogin_AndReEnablingRestoresIt()
    {
        var tenant = SeedTenant(Email);
        (await _provisioning.EnsurePortalAccountForTenantAsync(tenant.Id, PortfolioId)).Status
            .Should().Be(PortalAccountStatus.Created);
        var password = new SeedSettings().TenantPassword;
        var auth = CreateAuthService();

        // Baseline: a freshly provisioned tenant can sign in.
        (await auth.LoginAsync(Email, password)).Success.Should().BeTrue();

        // Disable → the login is rejected with a "portal access off" message.
        (await _provisioning.SetPortalAccessAsync(tenant.Id, PortfolioId, enabled: false)).Access
            .Should().Be(TenantPortalAccess.Disabled);
        var blocked = await auth.LoginAsync(Email, password);
        blocked.Success.Should().BeFalse("a tenant whose portal access is turned off cannot sign in");
        blocked.Error.Should().Contain("portal access");

        // Re-enable → the login works again.
        (await _provisioning.SetPortalAccessAsync(tenant.Id, PortfolioId, enabled: true)).Access
            .Should().Be(TenantPortalAccess.Active);
        (await auth.LoginAsync(Email, password)).Success.Should().BeTrue("re-enabling restores the tenant's sign-in");
    }

    private Tenant SeedTenant(string email)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Toggle",
            LastName = "Tenant",
            Email = email,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private AuthService CreateAuthService()
    {
        var tokenService = new Mock<IJwtTokenService>();
        tokenService
            .Setup(t => t.GenerateTokensAsync(
                It.IsAny<ApplicationUser>(), It.IsAny<IList<string>>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new TokenResult { AccessToken = "test", AccessTokenExpiration = DateTime.UtcNow.AddMinutes(15) });

        var userMigration = new Mock<IUserMigrationService>();
        userMigration.Setup(m => m.RequiresPasswordResetAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(false);

        return new AuthService(
            _userManager,
            CreateSignInManager(_userManager),
            tokenService.Object,
            userMigration.Object,
            Mock.Of<IAuthEmailSender>(),
            _ctx.Db,
            new AuditTrailService(_ctx.Db, new AuditScope(), TimeProvider.System),
            Mock.Of<ISelfOwnerProvisioner>(),
            NullLogger<AuthService>.Instance,
            TimeProvider.System);
    }

    private static SignInManager<ApplicationUser> CreateSignInManager(UserManager<ApplicationUser> userManager)
        => new(
            userManager,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<ApplicationUser>>(),
            Options.Create(new IdentityOptions()),
            NullLogger<SignInManager<ApplicationUser>>.Instance,
            Mock.Of<IAuthenticationSchemeProvider>(),
            Mock.Of<IUserConfirmation<ApplicationUser>>());

    private static UserManager<ApplicationUser> CreateUserManager(RentalCommandDbContext db)
    {
        var store = new UserStore<ApplicationUser, IdentityRole<int>, RentalCommandDbContext, int>(db);
        return new UserManager<ApplicationUser>(
            store,
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
    }
}
