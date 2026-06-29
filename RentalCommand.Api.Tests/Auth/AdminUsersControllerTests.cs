using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
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
        _ctx.Db.Users.Add(new ApplicationUser
        {
            Id = 7,
            PortfolioId = PortfolioId,
            UserName = "admin@example.local",
            NormalizedUserName = "ADMIN@EXAMPLE.LOCAL",
            Email = "admin@example.local",
            NormalizedEmail = "ADMIN@EXAMPLE.LOCAL",
            DisplayName = "Admin User",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
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
    public async Task Create_TenantRole_IsRejectedAndCreatesNoIdentityUser()
    {
        // Tenants are provisioned from the Tenants section, never minted as a staff/team role here —
        // even with a valid tenant link. This guard closes the "broken tenant login from the team UI" bug.
        var tenant = SeedTenant(id: 42);
        var userManager = CreateUserManagerMock();
        userManager
            .Setup(m => m.FindByEmailAsync("portal@example.local"))
            .ReturnsAsync((ApplicationUser?)null);

        var controller = CreateController(userManager.Object);

        var result = await controller.Create(new CreateTeamMemberRequest
        {
            Email = "portal@example.local",
            DisplayName = "Portal Tenant",
            Role = UserRole.Tenant,
            TenantId = tenant.Id,
            TemporaryPassword = "Pass85Tenant!23",
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        userManager.Verify(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        _ctx.Db.UserAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangeRole_ToTenantRole_IsRejected()
    {
        SeedMember("promote-me@example.local", PortfolioId);
        await _ctx.Db.SaveChangesAsync();
        var account = _ctx.Db.UserAccounts.Single(u => u.Email == "promote-me@example.local");
        var controller = CreateController();

        var result = await controller.ChangeRole(
            account.Id, new ChangeRoleRequest { Role = UserRole.Tenant }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _ctx.Db.UserAccounts.Single(u => u.Id == account.Id).Role.Should().Be(UserRole.Manager);
    }

    [Fact]
    public async Task Create_TeamMember_WritesAuditLogWithoutTemporaryPassword()
    {
        var userManager = CreateUserManagerMock();
        userManager
            .Setup(m => m.FindByEmailAsync("audited@example.local"))
            .ReturnsAsync((ApplicationUser?)null);
        userManager
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .Callback<ApplicationUser, string>((u, _) => u.Id = 321)
            .ReturnsAsync(IdentityResult.Success);
        userManager
            .Setup(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), nameof(UserRole.Manager)))
            .ReturnsAsync(IdentityResult.Success);

        var controller = CreateController(userManager.Object);

        await controller.Create(new CreateTeamMemberRequest
        {
            Email = "audited@example.local",
            DisplayName = "Audited Manager",
            Role = UserRole.Manager,
            TemporaryPassword = "TempPass85!NoAudit",
        }, CancellationToken.None);

        var account = _ctx.Db.UserAccounts.Single(u => u.Email == "audited@example.local");
        var audit = _ctx.Db.AuditLogs.Should().ContainSingle().Subject;
        audit.PortfolioId.Should().Be(PortfolioId);
        audit.UserId.Should().Be(7);
        audit.EntityType.Should().Be(nameof(UserAccount));
        audit.EntityId.Should().Be(account.Id);
        audit.Operation.Should().Be(AuditLogOperation.Created);
        audit.NewValues.Should().Contain("\"email\":\"audited@example.local\"");
        audit.NewValues.Should().Contain("\"role\":\"Manager\"");
        audit.NewValues.Should().NotContain("TempPass85!NoAudit");
        audit.OldValues.Should().BeNull();
    }

    [Fact]
    public async Task Create_TeamMember_EnqueuesTeamInviteEmailWithSignInDetails()
    {
        var userManager = CreateUserManagerMock();
        userManager
            .Setup(m => m.FindByEmailAsync("invited@example.local"))
            .ReturnsAsync((ApplicationUser?)null);
        userManager
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .Callback<ApplicationUser, string>((u, _) => u.Id = 555)
            .ReturnsAsync(IdentityResult.Success);
        userManager
            .Setup(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), nameof(UserRole.Manager)))
            .ReturnsAsync(IdentityResult.Success);

        var controller = CreateController(userManager.Object);

        await controller.Create(new CreateTeamMemberRequest
        {
            Email = "invited@example.local",
            DisplayName = "Invited Manager",
            Role = UserRole.Manager,
            TemporaryPassword = "Invite85!Welcome",
        }, CancellationToken.None);

        // The Engine routes every outbox email under MessageType "email"; the invite is identified
        // by its payload (recipient, the temporary password, the login link, and the role).
        var outbox = _ctx.Db.OutboxMessages.Should().ContainSingle().Subject;
        outbox.MessageType.Should().Be("email");
        outbox.Payload.Should().Contain("invited@example.local");
        outbox.Payload.Should().Contain("Invite85!Welcome");
        outbox.Payload.Should().Contain("/login");
        outbox.Payload.Should().Contain("Manager");
    }

    [Fact]
    public async Task ChangeRole_WritesAuditLogWithOldAndNewRole()
    {
        SeedMember("role-change@example.local", PortfolioId);
        await _ctx.Db.SaveChangesAsync();
        var account = _ctx.Db.UserAccounts.Single(u => u.Email == "role-change@example.local");
        var userManager = CreateUserManagerMock();
        userManager
            .Setup(m => m.FindByEmailAsync(account.Email))
            .ReturnsAsync((ApplicationUser?)null);
        var controller = CreateController(userManager.Object);

        await controller.ChangeRole(account.Id, new ChangeRoleRequest { Role = UserRole.Agent }, CancellationToken.None);

        var audit = _ctx.Db.AuditLogs.Should().ContainSingle().Subject;
        audit.PortfolioId.Should().Be(PortfolioId);
        audit.UserId.Should().Be(7);
        audit.EntityType.Should().Be(nameof(UserAccount));
        audit.EntityId.Should().Be(account.Id);
        audit.Operation.Should().Be(AuditLogOperation.Updated);
        audit.OldValues.Should().Contain("\"role\":\"Manager\"");
        audit.NewValues.Should().Contain("\"role\":\"Agent\"");
        audit.ChangeReason.Should().Contain("role");
    }

    [Fact]
    public async Task SetActive_WritesAuditLogsForDeactivateAndReactivate()
    {
        SeedMember("active-toggle@example.local", PortfolioId);
        await _ctx.Db.SaveChangesAsync();
        var account = _ctx.Db.UserAccounts.Single(u => u.Email == "active-toggle@example.local");
        var controller = CreateController();

        await controller.SetActive(account.Id, new SetActiveRequest { IsActive = false }, CancellationToken.None);
        controller = CreateController();
        await controller.SetActive(account.Id, new SetActiveRequest { IsActive = true }, CancellationToken.None);

        var audits = _ctx.Db.AuditLogs
            .Where(a => a.EntityType == nameof(UserAccount) && a.EntityId == account.Id)
            .OrderBy(a => a.Id)
            .ToList();
        audits.Should().HaveCount(2);
        audits.Should().OnlyContain(a =>
            a.PortfolioId == PortfolioId &&
            a.UserId == 7 &&
            a.Operation == AuditLogOperation.Updated);
        audits[0].OldValues.Should().Contain("\"isActive\":true");
        audits[0].NewValues.Should().Contain("\"isActive\":false");
        audits[0].ChangeReason.Should().Contain("deactivated");
        audits[1].OldValues.Should().Contain("\"isActive\":false");
        audits[1].NewValues.Should().Contain("\"isActive\":true");
        audits[1].ChangeReason.Should().Contain("reactivated");
    }

    private AdminUsersController CreateController(UserManager<ApplicationUser>? userManager = null) => new(
        userManager ?? CreateUserManagerMock().Object,
        _ctx.Db,
        new AuditTrailService(_ctx.Db, new AuditScope()),
        // Real outbox sender over the test Db so we can assert the invite email is enqueued.
        new OutboxAuthEmailSender(_ctx.Db, new ConfigurationBuilder().Build(), NullLogger<OutboxAuthEmailSender>.Instance),
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
