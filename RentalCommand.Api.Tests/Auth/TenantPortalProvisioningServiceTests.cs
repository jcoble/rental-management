using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

public class TenantPortalProvisioningServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task EnsurePortalAccount_TenantWithoutEmail_ReturnsNoEmail_AndCreatesNoUser()
    {
        var tenant = SeedTenant(id: 10, email: null);
        var userManager = CreateUserManagerMock();
        var service = CreateService(userManager.Object);

        var result = await service.EnsurePortalAccountForTenantAsync(tenant.Id, PortfolioId, CancellationToken.None);

        result.Status.Should().Be(PortalAccountStatus.NoEmail);
        userManager.Verify(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        _ctx.Db.UserAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsurePortalAccount_TenantInAnotherPortfolio_ReturnsTenantNotFound()
    {
        // IDOR guard: a tenant that exists but in a different portfolio must look like "not found".
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = 2,
            Name = "Other",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();
        var foreign = SeedTenant(id: 20, email: "foreign@example.local", portfolioId: 2);

        var userManager = CreateUserManagerMock();
        var service = CreateService(userManager.Object);

        var result = await service.EnsurePortalAccountForTenantAsync(foreign.Id, PortfolioId, CancellationToken.None);

        result.Status.Should().Be(PortalAccountStatus.TenantNotFound);
        userManager.Verify(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EnsurePortalAccount_NewTenant_CreatesIdentityAndContextWithoutLegacyRoleOrAccount()
    {
        var tenant = SeedTenant(id: 30, email: "new.tenant@example.local");
        var userManager = CreateUserManagerMock();
        ApplicationUser? created = null;
        string? password = null;

        userManager
            .Setup(m => m.FindByEmailAsync("new.tenant@example.local"))
            .ReturnsAsync((ApplicationUser?)null);
        userManager
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .Callback<ApplicationUser, string>((u, pwd) =>
            {
                u.Id = 555;
                created = u;
                password = pwd;
                _ctx.Db.Users.Attach(u);
            })
            .ReturnsAsync(IdentityResult.Success);

        var service = CreateService(userManager.Object);
        var result = await service.EnsurePortalAccountForTenantAsync(tenant.Id, PortfolioId, CancellationToken.None);

        result.Status.Should().Be(PortalAccountStatus.Created);
        result.Email.Should().Be("new.tenant@example.local");

        created.Should().NotBeNull();
        created!.Email.Should().Be("new.tenant@example.local");
        created.PortfolioId.Should().Be(PortfolioId);
        created.EmailConfirmed.Should().BeTrue();
        password.Should().Be(new SeedSettings().TenantPassword);
        _ctx.Db.WorkspaceAccessContexts.Should().ContainSingle(context =>
            context.UserId == created.Id && context.PortfolioId == PortfolioId);
        _ctx.Db.TenantUserAccesses.Should().BeEmpty(
            "portal authority begins only when an effective lease party exists");
        _ctx.Db.UserAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsurePortalAccount_ExistingIdentityUser_ReturnsAlreadyExisted_AndDoesNotRecreate()
    {
        var tenant = SeedTenant(id: 40, email: "has.login@example.local");
        var existing = new ApplicationUser
        {
            Id = 999,
            UserName = "has.login@example.local",
            Email = "has.login@example.local",
            EmailConfirmed = true,
            DisplayName = "Has Login",
            PortfolioId = PortfolioId,
        };
        _ctx.Db.Users.Add(existing);
        _ctx.Db.SaveChanges();

        var userManager = CreateUserManagerMock();
        userManager
            .Setup(m => m.FindByEmailAsync("has.login@example.local"))
            .ReturnsAsync(existing);
        var service = CreateService(userManager.Object);
        var result = await service.EnsurePortalAccountForTenantAsync(tenant.Id, PortfolioId, CancellationToken.None);

        result.Status.Should().Be(PortalAccountStatus.AlreadyExisted);
        userManager.Verify(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
    }

    private TenantPortalProvisioningService CreateService(UserManager<ApplicationUser> userManager) => new(
        userManager,
        _ctx.Db,
        Options.Create(new SeedSettings()),
        TimeProvider.System,
        NullLogger<TenantPortalProvisioningService>.Instance);

    private static Mock<UserManager<ApplicationUser>> CreateUserManagerMock()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    private Tenant SeedTenant(int id, string? email, int portfolioId = PortfolioId)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = id,
            PortfolioId = portfolioId,
            FirstName = "Test",
            LastName = "Tenant",
            Email = email,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }
}
