using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Data;
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
    public async Task LegacyTenantRoleLogin_DoesNotBypassCanonicalAccessContext()
    {
        var tenant = SeedTenant(Email);
        (await _provisioning.EnsurePortalAccountForTenantAsync(tenant.Id, PortfolioId)).Status
            .Should().Be(PortalAccountStatus.Created);
        var password = new SeedSettings().TenantPassword;
        var auth = CreateAuthService();

        var baseline = await auth.LoginAsync(Email, password);
        baseline.Success.Should().BeFalse();
        baseline.Error.Should().Contain("no active workspace access");

        // With no effective lease party there is no relationship grant to revoke.
        (await _provisioning.SetPortalAccessAsync(tenant.Id, PortfolioId, enabled: false)).Access
            .Should().Be(TenantPortalAccess.None);
        var blocked = await auth.LoginAsync(Email, password);
        blocked.Success.Should().BeFalse("a login without an effective relationship cannot sign in");
        blocked.Error.Should().Contain("no active workspace access");

        // Enabling cannot manufacture a relationship when no effective lease party exists.
        (await _provisioning.SetPortalAccessAsync(tenant.Id, PortfolioId, enabled: true)).Access
            .Should().Be(TenantPortalAccess.None);
        var reenabled = await auth.LoginAsync(Email, password);
        reenabled.Success.Should().BeFalse();
        reenabled.Error.Should().Contain("no active workspace access");
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
        var userMigration = new Mock<IUserMigrationService>();
        userMigration.Setup(m => m.RequiresPasswordResetAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(false);
        var contextSelection = new Mock<RentalCommand.Core.Authorization.IEffectiveAccessContextSelectionQuery>();
        contextSelection
            .Setup(query => query.ListAsync(
                It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RentalCommand.Core.Authorization.EffectiveAccessContextOption>());

        return new AuthService(
            _userManager,
            CreateSignInManager(_userManager),
            Mock.Of<IAtomicAuthSessionCredentialService>(),
            Mock.Of<ICanonicalAccessTokenService>(),
            contextSelection.Object,
            Mock.Of<RentalCommand.Core.Authorization.IAccessEnvelopeQuery>(),
            Options.Create(new AtomicAuthSessionCredentialOptions
            {
                SigningKey = Convert.ToBase64String(new byte[32]),
                CredentialLifetimeDays = 7,
                FamilyAbsoluteLifetimeDays = 30,
                SessionLifetimeDays = 30,
            }),
            userMigration.Object,
            Mock.Of<IAuthEmailSender>(),
            _ctx.Db,
            new AuditTrailService(_ctx.Db, new AuditScope(), TimeProvider.System),
            Mock.Of<ICanonicalAccountBootstrapService>(),
            new RlsExecutionContext(),
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
