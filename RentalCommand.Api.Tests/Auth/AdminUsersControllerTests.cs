using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

public class AdminUsersControllerTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;

    public AdminUsersControllerTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = 2,
            Name = "Other Portfolio",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPage_ReturnsSqlCountAndRequestedWindow()
    {
        SeedMembers("alpha@example.local", "bravo@example.local", "charlie@example.local", "delta@example.local");
        SeedMember("aaa-foreign@example.local", portfolioId: 2);

        var controller = CreateController();

        _commands.Clear();
        var result = await controller.ListPage(new ListQuery
        {
            Sort = "email",
            Skip = 1,
            Take = 2,
        }, CancellationToken.None);

        result.Value.Should().NotBeNull();
        var page = result.Value!;
        page.TotalCount.Should().Be(4);
        page.Skip.Should().Be(1);
        page.Take.Should().Be(2);
        page.Items.Select(m => m.Email).Should().Equal("bravo@example.local", "charlie@example.local");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"UserAccounts\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"Email\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_TenantMember_RequiresTenantLinkBeforeCreatingIdentityUser()
    {
        var userManager = CreateUserManagerMock();
        userManager
            .Setup(m => m.FindByEmailAsync("new-tenant@example.local"))
            .ReturnsAsync((ApplicationUser?)null);

        var controller = CreateController(userManager.Object);

        var result = await controller.Create(new CreateTeamMemberRequest
        {
            Email = "new-tenant@example.local",
            DisplayName = "New Tenant",
            Role = UserRole.Tenant,
            TemporaryPassword = "Pass85Tenant!23",
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        userManager.Verify(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        _ctx.Db.UserAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_TenantMember_PersistsTenantLinkToIdentityUserAndUserAccount()
    {
        var tenant = SeedTenant(id: 42);
        var userManager = CreateUserManagerMock();
        ApplicationUser? identityUser = null;
        string? identityPassword = null;
        string? identityRole = null;

        userManager
            .Setup(m => m.FindByEmailAsync("portal@example.local"))
            .ReturnsAsync((ApplicationUser?)null);
        userManager
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .Callback<ApplicationUser, string>((u, password) =>
            {
                u.Id = 123;
                identityUser = u;
                identityPassword = password;
            })
            .ReturnsAsync(IdentityResult.Success);
        userManager
            .Setup(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), nameof(UserRole.Tenant)))
            .Callback<ApplicationUser, string>((_, role) => identityRole = role)
            .ReturnsAsync(IdentityResult.Success);

        var controller = CreateController(userManager.Object);

        var result = await controller.Create(new CreateTeamMemberRequest
        {
            Email = "portal@example.local",
            DisplayName = "Portal Tenant",
            Role = UserRole.Tenant,
            TenantId = tenant.Id,
            TemporaryPassword = "Pass85Tenant!23",
        }, CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var response = created.Value.Should().BeOfType<CreateTeamMemberResponse>().Subject;

        identityUser.Should().NotBeNull();
        identityUser!.Email.Should().Be("portal@example.local");
        identityUser.PortfolioId.Should().Be(PortfolioId);
        identityUser.TenantId.Should().Be(tenant.Id);
        identityUser.EmailConfirmed.Should().BeTrue();
        identityPassword.Should().Be("Pass85Tenant!23");
        identityRole.Should().Be(nameof(UserRole.Tenant));

        var account = _ctx.Db.UserAccounts.Single(u => u.Email == "portal@example.local");
        account.PortfolioId.Should().Be(PortfolioId);
        account.TenantId.Should().Be(tenant.Id);
        account.Role.Should().Be(UserRole.Tenant);
        account.IsActive.Should().BeTrue();

        response.Member.Email.Should().Be("portal@example.local");
        response.Member.TenantId.Should().Be(tenant.Id);
        response.Member.Role.Should().Be(nameof(UserRole.Tenant));
        response.GeneratedPassword.Should().BeNull();
    }

    private AdminUsersController CreateController(UserManager<ApplicationUser>? userManager = null) => new(
        userManager ?? CreateUserManagerMock().Object,
        _ctx.Db,
        NullLogger<AdminUsersController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("portfolioId", PortfolioId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, "7"),
                    new Claim(ClaimTypes.Role, "Admin"),
                ], "test")),
            },
        },
    };

    private static Mock<UserManager<ApplicationUser>> CreateUserManagerMock()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new Mock<UserManager<ApplicationUser>>(
            store.Object,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
    }

    private Tenant SeedTenant(int id)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = id,
            PortfolioId = PortfolioId,
            FirstName = "Portal",
            LastName = "Tenant",
            Email = "portal.tenant@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private void SeedMembers(params string[] emails)
    {
        foreach (var email in emails)
        {
            SeedMember(email, PortfolioId);
        }
        _ctx.Db.SaveChanges();
    }

    private void SeedMember(string email, int portfolioId)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.UserAccounts.Add(new UserAccount
        {
            PortfolioId = portfolioId,
            Email = email,
            DisplayName = email.Split('@')[0],
            PasswordHash = string.Empty,
            Role = UserRole.Manager,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
